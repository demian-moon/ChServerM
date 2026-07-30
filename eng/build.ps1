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
    [switch]$SkipAot
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

if ($SkipTests) {
    Write-Step 'test'
    Write-Host 'SKIPPED — -SkipTests 지정됨' -ForegroundColor Yellow
}
else {
    Invoke-Step 'test' {
        dotnet test $Solution --configuration $Configuration --no-build --verbosity minimal
    }
}

# ──────────────────────────────────────────────────────────────
# Native AOT 검증
#
# 라이브러리는 AOT publish 대상이 아니다. IsAotCompatible=true 로 분석기가 이미
# 컴파일 타임에 검사하지만, 그것과 실제 AOT 컴파일 성공은 다른 문제다.
# 실행 가능 프로젝트가 생기면(Samples, Phase 2+) 여기서 실제로 검증한다.
# ──────────────────────────────────────────────────────────────
Write-Step 'aot'

if ($SkipAot) {
    Write-Host 'SKIPPED — -SkipAot 지정됨' -ForegroundColor Yellow
}
else {
    # @() 로 감싸는 것이 중요하다. Set-StrictMode 하에서 파이프라인 결과가
    # 없거나 단일 객체이면 .Count 접근이 예외를 던진다.
    $exeProjects = @(
        Get-ChildItem -Path $RepoRoot -Recurse -Filter '*.csproj' |
            Where-Object { $_.FullName -notlike '*\LegacyServer\*' } |
            Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match '<OutputType>\s*Exe\s*</OutputType>' }
    )

    if ($exeProjects.Count -eq 0) {
        # 조용히 통과시키지 않는다. 검증하지 않았다는 사실을 명시한다.
        Write-Host 'SKIPPED — 실행 가능 프로젝트가 아직 없다.' -ForegroundColor Yellow
        Write-Host '  라이브러리만 있는 상태에서는 AOT publish 를 수행할 수 없다.'
        Write-Host '  IsAotCompatible=true 로 AOT/트리밍 분석기는 build 단계에서 이미 적용됨.'
        Write-Host '  실제 AOT 컴파일 검증은 Samples 실행 프로젝트 추가 시(Phase 2+) 활성화된다.'
    }
    else {
        foreach ($proj in $exeProjects) {
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
