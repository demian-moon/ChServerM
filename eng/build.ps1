#!/usr/bin/env pwsh
<#
.SYNOPSIS
    ChServerM 빌드·검증 파이프라인. 로컬과 CI가 같은 스크립트를 쓴다.

.DESCRIPTION
    CI에서만 통과하거나 로컬에서만 통과하는 상황을 막기 위해 단일 진입점으로 둔다.
    경고는 CI 모드에서 오류로 승격된다 — 성능 분석기(CA18xx)를 경고로 두면 무시되기 때문이다.

.PARAMETER Configuration
    Debug 또는 Release. 벤치마크와 AOT 검증은 Release 에서만 의미가 있다.

.PARAMETER WarnAsError
    경고를 오류로 승격한다. CI 에서는 항상 켠다.

.EXAMPLE
    ./eng/build.ps1
    ./eng/build.ps1 -Configuration Debug -SkipAot
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$WarnAsError,
    [switch]$SkipTests,
    [switch]$SkipAot,

    # 취약점 감사는 NuGet 취약점 DB 조회가 필요하다. 오프라인에서는 건너뛴다.
    [switch]$SkipAudit,

    # 커버리지 수집은 테스트를 느리게 만든다. CI 에서는 켜고 로컬 반복에서는 끈다.
    [switch]$Coverage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Windows PowerShell 5.1 은 기본 콘솔 인코딩이 CP949 라 한글이 깨진다.
# 구분선에 박스 문자를 쓰지 않는 것도 같은 이유다(5.1 에서 mojibake).
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Solution = Join-Path $RepoRoot 'ChServerM.slnx'

$script:StepIndex = 0
function Write-Step {
    param([string]$Name)
    $script:StepIndex++
    Write-Host ''
    Write-Host "== [$script:StepIndex] $Name " -NoNewline -ForegroundColor Cyan
    Write-Host ('=' * [Math]::Max(0, 60 - $Name.Length)) -ForegroundColor DarkGray
}

# StrictMode 에서 없는 속성에 접근하면 예외가 난다.
# JSON 은 값이 없는 키를 아예 생략하므로 이 함수 없이 순회하면 터진다 —
# 그리고 그 예외가 exit 1 앞에서 나면 "발견했는데 통과"가 된다.
# 취약점 감사에서 실제로 그 일이 있었다.
function Get-JsonProperty {
    param([object]$Object, [string]$Name)

    if ($null -eq $Object) { return @() }
    if ($Object.PSObject.Properties.Name -notcontains $Name) { return @() }

    $value = $Object.$Name
    if ($null -eq $value) { return @() }

    return @($value)
}

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)
    Write-Step $Name
    & $Action
    if ($LASTEXITCODE -ne 0) {
        Write-Host "실패: $Name (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

Write-Host "ChServerM 빌드" -ForegroundColor Green
Write-Host "  구성       : $Configuration"
Write-Host "  솔루션     : $Solution"
Write-Host "  경고→오류  : $($WarnAsError.IsPresent)"
Write-Host "  SDK        : $(dotnet --version)"

$buildArgs = @($Solution, '--configuration', $Configuration, '--no-restore')
if ($WarnAsError) { $buildArgs += '/warnaserror' }

Invoke-Step 'restore' { dotnet restore $Solution }
Invoke-Step 'build'   { dotnet build @buildArgs }

$CoverageDir = Join-Path $RepoRoot 'artifacts/coverage'

if ($SkipTests) {
    Write-Step 'test'
    Write-Host 'SKIPPED — -SkipTests 지정됨' -ForegroundColor Yellow
}
else {
    $testArgs = @($Solution, '--configuration', $Configuration, '--no-build', '--verbosity', 'minimal')

    if ($Coverage) {
        # 산출물을 매번 새로 만든다. 남아 있는 이전 결과를 합치면
        # "커버리지가 올랐다"는 착각이 생긴다.
        if (Test-Path -LiteralPath $CoverageDir) {
            Remove-Item -LiteralPath $CoverageDir -Recurse -Force
        }
        New-Item -ItemType Directory -Path $CoverageDir -Force | Out-Null

        $testArgs += @('--collect:XPlat Code Coverage', '--results-directory', $CoverageDir)
    }

    Invoke-Step 'test' { dotnet test @testArgs }
}

# ──────────────────────────────────────────────────────────────
# 커버리지 요약
#
# 임계값은 아직 세우지 않는다 (ROADMAP Phase 0: "임계값은 Core 추상화 확정 후 설정").
# 지금 목적은 수치를 보이게 만드는 것이다 — 보이지 않으면 관리되지 않는다.
# ──────────────────────────────────────────────────────────────
if ($Coverage -and -not $SkipTests) {
    Write-Step 'coverage'

    $reports = @(Get-ChildItem -Path $CoverageDir -Recurse -Filter 'coverage.cobertura.xml' -ErrorAction SilentlyContinue)

    if ($reports.Count -eq 0) {
        # 조용히 넘기지 않는다. 수집이 실패했는데 통과로 보이는 것이 최악이다.
        Write-Host '실패: 커버리지 파일을 찾지 못했다. coverlet.collector 참조를 확인한다.' -ForegroundColor Red
        exit 1
    }

    # 파일 단위가 아니라 어셈블리 단위로 집계한다.
    # cobertura 파일명은 GUID 라 그대로 찍으면 "어느 어셈블리가 얼마인지"를 알 수 없고,
    # 그러면 수치가 있어도 관리에 쓸 수 없다.
    $byAssembly = @{}

    foreach ($report in $reports) {
        [xml]$xml = Get-Content -LiteralPath $report.FullName

        foreach ($package in @($xml.coverage.packages.package)) {
            if ($null -eq $package) { continue }

            $name = [string]$package.name

            # 같은 어셈블리가 여러 테스트 프로젝트에서 측정되면 가장 높은 값을 남긴다.
            # 합집합 커버리지를 정확히 구하려면 ReportGenerator 가 필요하다 —
            # 지금은 근사이고, 그 사실을 아래에 명시한다.
            $line = [double]$package.'line-rate'
            $branch = [double]$package.'branch-rate'

            if (-not $byAssembly.ContainsKey($name) -or $byAssembly[$name].Line -lt $line) {
                $byAssembly[$name] = @{ Line = $line; Branch = $branch }
            }
        }
    }

    foreach ($name in ($byAssembly.Keys | Sort-Object)) {
        $entry = $byAssembly[$name]
        Write-Host ("  {0,-34} line {1,6:P1}  branch {2,6:P1}" -f $name, $entry.Line, $entry.Branch)
    }

    Write-Host ''
    Write-Host "  cobertura 파일 $($reports.Count)개: $CoverageDir" -ForegroundColor DarkGray
    Write-Host '  ⚠ 어셈블리별 최대값이다. 여러 테스트 프로젝트의 합집합이 아니다 —' -ForegroundColor DarkGray
    Write-Host '    정확한 합집합은 ReportGenerator 도입 후에 구한다' -ForegroundColor DarkGray
    Write-Host '  임계값은 미설정 — Core 추상화가 확정된 뒤 세운다 (ROADMAP Phase 0)' -ForegroundColor DarkGray
}

# ──────────────────────────────────────────────────────────────
# NuGet 취약점 감사 (Phase 0 게이트 조건)
#
# ⚠ 이 명령에는 함정이 둘 있다. 둘 다 조용한 실패로 이어진다.
#
#   1. 취약점이 발견돼도 exit code 가 0 이다.
#      naive 하게 호출하면 CI 가 통과한다 — 감사를 안 한 것과 같다
#   2. 사람이 읽는 출력은 로케일에 따라 달라진다.
#      한국어 개발 머신과 영어 CI 러너에서 grep 대상이 다르다
#
# 그래서 --format json 으로 받아 파싱한다. 그리고 "감사가 실제로 수행됐는지"까지
# 확인한다 — 오프라인이면 취약점 DB 조회가 안 되고, 그때 결과가 비어 있는 것을
# "안전함"으로 읽으면 안 된다.
# ──────────────────────────────────────────────────────────────
Write-Step 'audit'

if ($SkipAudit) {
    Write-Host 'SKIPPED — -SkipAudit 지정됨 (취약점 DB 조회 불가 환경)' -ForegroundColor Yellow
}
else {
    $auditJson = dotnet list package --vulnerable --include-transitive --format json 2>&1 | Out-String

    if ($LASTEXITCODE -ne 0) {
        Write-Host "실패: 취약점 감사 명령이 오류로 끝났다 (exit $LASTEXITCODE)" -ForegroundColor Red
        Write-Host $auditJson
        exit $LASTEXITCODE
    }

    try {
        $audit = $auditJson | ConvertFrom-Json
    }
    catch {
        Write-Host '실패: 취약점 감사 출력을 JSON 으로 파싱할 수 없다.' -ForegroundColor Red
        Write-Host $auditJson
        exit 1
    }

    # 감사가 실제로 수행됐는지 확인한다 — 빈 결과를 "안전함"으로 읽지 않기 위해서다.
    $projects = @($audit.projects)
    if ($projects.Count -eq 0) {
        Write-Host '실패: 감사 대상 프로젝트가 0개다. 감사가 수행되지 않았다.' -ForegroundColor Red
        exit 1
    }

    $remoteSources = @(@($audit.sources) | Where-Object { $_ -like 'http*' })
    if ($remoteSources.Count -eq 0) {
        Write-Host '실패: 원격 NuGet 소스가 없다. 취약점 DB 를 조회할 수 없으므로 결과를 신뢰할 수 없다.' -ForegroundColor Red
        Write-Host '  오프라인 환경이라면 -SkipAudit 을 명시한다. 조용히 통과시키지 않는다.' -ForegroundColor Red
        exit 1
    }

    # 취약점이 있는 프로젝트만 frameworks 배열을 갖는다. 없으면 path 만 있다.
    #
    # StrictMode 에서는 없는 속성에 접근하면 던지므로 PSObject 로 존재를 먼저 확인한다.
    # $_.frameworks 를 그대로 쓰면 "취약점 없음"이 예외가 된다 — 통과해야 할 때 실패한다.
    # @() 로 감싸는 것이 중요하다. PowerShell 은 함수가 돌려준 빈 배열을 언랩해
    # $null 로 만들고, StrictMode 에서 $null.Count 는 예외다.
    # 이 프로젝트에서 이미 한 번 밟은 함정이다.
    $findings = @($projects | Where-Object { @(Get-JsonProperty $_ 'frameworks').Count -gt 0 })

    if ($findings.Count -gt 0) {
        Write-Host "취약한 패키지가 발견됐다 (프로젝트 $($findings.Count)개)" -ForegroundColor Red

        foreach ($project in $findings) {
            Write-Host "  $($project.path)" -ForegroundColor Red

            foreach ($framework in (Get-JsonProperty $project 'frameworks')) {
                $packages = @(Get-JsonProperty $framework 'topLevelPackages') +
                            @(Get-JsonProperty $framework 'transitivePackages')

                foreach ($package in $packages) {
                    foreach ($v in (Get-JsonProperty $package 'vulnerabilities')) {
                        Write-Host "    $($package.id) $($package.resolvedVersion) — $($v.severity) $($v.advisoryurl)" -ForegroundColor Red
                    }
                }
            }
        }

        exit 1
    }

    Write-Host "취약점 없음 — 프로젝트 $($projects.Count)개 검사 (소스: $($remoteSources -join ', '))" -ForegroundColor Green
}

# ──────────────────────────────────────────────────────────────
# Native AOT 검증
#
# 라이브러리는 AOT publish 대상이 아니다. IsAotCompatible=true 로 분석기가 이미
# 컴파일 타임에 검사하지만, 그것과 실제 AOT 컴파일 성공은 다른 문제다.
#
# 대상 선정 기준은 <PublishAot>true</PublishAot> 다. OutputType=Exe 로 고르면
# 벤치마크 프로젝트까지 잡히는데, BenchmarkDotNet 은 런타임에 코드를 생성·컴파일하므로
# AOT 대상이 아니다. "실행 가능한가"가 아니라 "AOT 로 배포할 의도가 있는가"가 기준이다.
# ──────────────────────────────────────────────────────────────
Write-Step 'aot'

if ($SkipAot) {
    Write-Host 'SKIPPED — -SkipAot 지정됨' -ForegroundColor Yellow
}
else {
    # @() 로 감싸는 것이 중요하다. Set-StrictMode 하에서 파이프라인 결과가
    # 없거나 단일 객체이면 .Count 접근이 예외를 던진다.
    $aotProjects = @(
        Get-ChildItem -Path $RepoRoot -Recurse -Filter '*.csproj' |
            # 레거시 트리는 저장소에 있지만 솔루션에 없고 빌드 대상이 아니다.
            # 여기서 걸러내지 않으면 AOT 단계가 승계하지 않는 코드를 컴파일하려 든다.
            Where-Object { $_.FullName -notlike '*LegacyServer*' -and $_.FullName -notlike '*LagacyClient*' } |
            Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match '<PublishAot>\s*true\s*</PublishAot>' }
    )

    if ($aotProjects.Count -eq 0) {
        # 조용히 통과시키지 않는다. 검증하지 않았다는 사실을 명시한다.
        Write-Host 'SKIPPED — PublishAot=true 를 선언한 프로젝트가 없다.' -ForegroundColor Yellow
        Write-Host '  IsAotCompatible=true 로 AOT/트리밍 분석기는 build 단계에서 이미 적용됐지만,'
        Write-Host '  실제 네이티브 링크가 성공하는지는 publish 해봐야 안다.'
        Write-Host '  AOT 로 배포할 프로젝트에 <PublishAot>true</PublishAot> 를 선언한다.'
    }
    else {
        # ILCompiler 는 Windows 에서 네이티브 링크 단계에 vswhere.exe 로 MSVC 링커를 찾는다.
        # Visual Studio 개발자 셸이 아닌 환경(일반 PowerShell, Git Bash, 일부 CI 이미지)에서는
        # vswhere 가 PATH 에 없어 "'vswhere.exe'은(는) 내부 또는 외부 명령이 아닙니다" 로 실패한다.
        # 컴파일은 이미 성공한 뒤라 원인이 코드처럼 보이는 것이 문제다 — 여기서 미리 막는다.
        #
        # Windows 에서만 한다. 리눅스·macOS 에는 ProgramFiles(x86) 환경 변수가 없어
        # Join-Path 가 null 을 받고 던진다 (StrictMode + ErrorActionPreference=Stop).
        # $IsWindows 를 쓰지 않는 이유: PowerShell 5.1 에는 없는 변수라 StrictMode 에서 터진다.
        $onWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)

        if ($onWindows) {
            $programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
            if ($programFilesX86) {
                $vsInstaller = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer'
                if ((Test-Path -LiteralPath (Join-Path $vsInstaller 'vswhere.exe')) -and
                    ($env:PATH -notlike "*$vsInstaller*")) {
                    $env:PATH = "$env:PATH;$vsInstaller"
                    Write-Host "vswhere 경로를 PATH 에 추가: $vsInstaller" -ForegroundColor DarkGray
                }
            }
        }

        foreach ($proj in $aotProjects) {
            Write-Host "AOT publish: $($proj.Name)" -ForegroundColor DarkCyan
            dotnet publish $proj.FullName `
                --configuration Release `
                --property:PublishAot=true `
                --property:TreatWarningsAsErrors=true
            if ($LASTEXITCODE -ne 0) {
                Write-Host "AOT 컴파일 실패: $($proj.Name)" -ForegroundColor Red
                exit $LASTEXITCODE
            }
        }
    }
}

Write-Host ''
Write-Host '모든 단계 통과' -ForegroundColor Green
