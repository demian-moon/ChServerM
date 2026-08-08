<#
.SYNOPSIS
    성능 회귀 게이트 — 벤치마크 팔 사이의 비율이 기준을 넘으면 실패한다.

.DESCRIPTION
    존재 이유
    ---------
    측정만 하고 지키지 않으면 성능은 반드시 퇴화한다. `DispatchAllocationGateTests` 가
    할당을 지키고, 이 스크립트가 **시간**을 지킨다.

    왜 절대 시간이 아니라 비율인가
    ------------------------------
    CI 공용 러너(ubuntu/windows-latest)는 이웃 부하로 절대 시간이 20~30% 흔들린다.
    "기준 대비 N% 퇴화 시 실패" 를 순진하게 걸면 임계가 좁으면 플래키하고, 넓으면
    아무것도 못 잡는다. **같은 실행 안의 두 팔 비율**은 노이즈가 분자·분모에 함께
    실려 상당 부분 상쇄되므로 공용 러너에서도 판정할 수 있다.

    이 게이트가 못 잡는 것 (정직하게 명시한다)
    ------------------------------------------
    **두 팔이 함께 느려지는 회귀는 못 잡는다.** 예: 프레이밍 전체가 2배 느려지면
    단일 세그먼트/세그먼트 경계 비율은 그대로다. 절대 수치 회귀는 이 머신(ENV-B)에서
    전체 job 으로 재는 기준선(`docs/BENCHMARKS.md`)이 담당하며, 그것은 사람이 돌린다.
    이 한계를 알고 쓰는 것과 모르고 쓰는 것은 다르다.

.PARAMETER SpecPath
    게이트 명세 JSON. 기본값은 이 스크립트 옆의 bench-gate.json.

.PARAMETER SkipRun
    벤치마크를 다시 돌리지 않고 기존 BenchmarkDotNet.Artifacts 결과로만 판정한다.
    게이트 자신을 검증할 때 쓴다.

.PARAMETER ArtifactsPath
    BenchmarkDotNet 산출물 경로. 기본값은 저장소 루트의 BenchmarkDotNet.Artifacts.

.EXAMPLE
    ./eng/bench-gate.ps1
    ./eng/bench-gate.ps1 -SkipRun     # 이미 있는 결과로 판정만
#>
[CmdletBinding()]
param(
    [string]$SpecPath,
    [switch]$SkipRun,
    [string]$ArtifactsPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $SpecPath)      { $SpecPath      = Join-Path $PSScriptRoot 'bench-gate.json' }
if (-not $ArtifactsPath) { $ArtifactsPath = Join-Path $repoRoot 'BenchmarkDotNet.Artifacts' }

if (-not (Test-Path $SpecPath)) { throw "게이트 명세를 찾을 수 없다: $SpecPath" }
$spec = Get-Content $SpecPath -Raw -Encoding utf8 | ConvertFrom-Json

Write-Host '=== 성능 회귀 게이트 (비율 기준) ===' -ForegroundColor Cyan
Write-Host "명세: $SpecPath"
Write-Host "기준선 측정: $($spec.measuredOn)"
Write-Host ''

# --- 벤치마크 실행 -----------------------------------------------------------
# 명세가 참조하는 타입만 돌린다. 게이트는 매 PR 에서 도는 것이므로 전체 스위트를
# 돌리면 안 된다 — 게이트가 느려지면 사람이 게이트를 끄게 된다.
if (-not $SkipRun) {
    $types = $spec.gates | ForEach-Object { $_.type } | Sort-Object -Unique

    # ⚠ --filter 는 **한 번만** 쓰고 값을 여러 개 넘긴다. `--filter A --filter B` 처럼
    # 옵션을 반복하면 BenchmarkDotNet 이 아무것도 매칭하지 않고 **exit 0 으로 조용히
    # 끝난다** — 실패가 아니라 성공처럼 보이므로 게이트가 통째로 무력해진다.
    $filters = @('--filter')
    foreach ($t in $types) { $filters += "*$(($t -split '\.')[-1])*" }

    Write-Host "벤치마크 실행 (게이트 모드, Job.ShortRun): $($types.Count) 개 타입" -ForegroundColor Yellow
    Write-Host "  필터: $(($filters | Select-Object -Skip 1) -join ' ')" -ForegroundColor DarkGray

    if (Test-Path $ArtifactsPath) { Remove-Item $ArtifactsPath -Recurse -Force }

    # 게이트 모드: 짧은 job + JSON 내보내기 (Bench/ChServerM.Bench/BenchConfig.cs)
    $env:CHSM_BENCH_GATE = '1'

    # FlatSharp.Compiler 는 net9.0 툴이라 .NET 9 런타임이 없는 머신에서는 빌드 단계가
    # "You must install or update .NET to run this application" 으로 깨진다. CI 러너에는
    # 9 런타임이 있어 필요 없지만, SDK 10 만 설치한 개발 머신에서 게이트가 못 도는 것을 막는다.
    $previousRollForward = $env:DOTNET_ROLL_FORWARD
    if (-not $previousRollForward) { $env:DOTNET_ROLL_FORWARD = 'LatestMajor' }

    try {
        Push-Location $repoRoot
        try {
            & dotnet run -c Release --project (Join-Path $repoRoot 'Bench/ChServerM.Bench') -- @filters
            if ($LASTEXITCODE -ne 0) { throw "벤치마크 실행이 실패했다 (exit $LASTEXITCODE)" }
        }
        finally { Pop-Location }
    }
    finally {
        Remove-Item Env:\CHSM_BENCH_GATE -ErrorAction SilentlyContinue
        if (-not $previousRollForward) { Remove-Item Env:\DOTNET_ROLL_FORWARD -ErrorAction SilentlyContinue }
    }
    Write-Host ''
}

# --- 결과 적재 ---------------------------------------------------------------
$resultsDir = Join-Path $ArtifactsPath 'results'
if (-not (Test-Path $resultsDir)) {
    throw "벤치마크 결과가 없다: $resultsDir (-SkipRun 을 썼다면 먼저 한 번 실행한다)"
}

# 타입 → (Method|Parameters) → Mean(ns)
$means = @{}
foreach ($file in Get-ChildItem $resultsDir -Filter '*-report-full.json') {
    $report = Get-Content $file.FullName -Raw -Encoding utf8 | ConvertFrom-Json
    foreach ($b in $report.Benchmarks) {
        # JSON 의 Type 은 짧은 이름이다 — 명세는 완전 수식 이름을 쓰므로 Namespace 를 합친다.
        $key = "$($b.Namespace).$($b.Type)|$($b.Method)|$($b.Parameters)"
        $means[$key] = [double]$b.Statistics.Mean
    }
}

if ($means.Count -eq 0) { throw "결과 JSON 에서 벤치마크를 하나도 읽지 못했다: $resultsDir" }

function Get-Mean {
    param([string]$Type, [string]$Method, [string]$Parameters)

    # 명세가 parameters 를 지정하지 않으면 매개변수 없는 항목을 찾는다.
    $key = "$Type|$Method|$Parameters"
    if ($means.ContainsKey($key)) { return $means[$key] }

    # 진단을 돕는다 — 이름이 바뀌었을 때 "없다" 만 말하면 원인을 찾기 어렵다.
    $candidates = $means.Keys | Where-Object { $_ -like "$Type|$Method|*" }
    if ($candidates) {
        throw "벤치마크를 찾았지만 매개변수가 맞지 않는다: $key`n  후보: $($candidates -join ', ')"
    }
    throw "벤치마크를 찾을 수 없다: $key (벤치 메서드 이름이 바뀌었다면 명세도 함께 고친다)"
}

# --- 판정 -------------------------------------------------------------------
$failures = @()
$rows = @()

foreach ($gate in $spec.gates) {
    $parameters = if ($gate.PSObject.Properties.Name -contains 'parameters') { $gate.parameters } else { '' }

    $num = Get-Mean -Type $gate.type -Method $gate.numerator   -Parameters $parameters
    $den = Get-Mean -Type $gate.type -Method $gate.denominator -Parameters $parameters

    if ($den -le 0) { throw "$($gate.id): 분모가 0 이하다 ($den) — 측정이 잘못됐다" }

    $ratio = $num / $den
    $passed = $ratio -le $gate.maxRatio
    # 기준선 대비 드리프트. 통과했더라도 임계에 근접하면 사람이 봐야 한다.
    $drift = if ($gate.baselineRatio -gt 0) { ($ratio / $gate.baselineRatio - 1) * 100 } else { 0 }

    $rows += [pscustomobject]@{
        Gate      = $gate.id
        Ratio     = '{0:F3}' -f $ratio
        Max       = '{0:F3}' -f $gate.maxRatio
        Baseline  = '{0:F3}' -f $gate.baselineRatio
        Drift     = '{0:+0.0;-0.0;0.0}%' -f $drift
        Result    = if ($passed) { 'PASS' } else { 'FAIL' }
    }

    if (-not $passed) {
        $failures += [pscustomobject]@{ Gate = $gate; Ratio = $ratio; Numerator = $num; Denominator = $den }
    }
}

$rows | Format-Table -AutoSize | Out-String | Write-Host

if ($failures.Count -eq 0) {
    Write-Host "게이트 통과 — 비율 $($spec.gates.Count) 건 전부 기준 이내." -ForegroundColor Green

    # 임계에 근접한 항목은 통과여도 알린다. 조용히 다가가는 퇴화를 놓치지 않기 위해서다.
    foreach ($gate in $spec.gates) {
        $parameters = if ($gate.PSObject.Properties.Name -contains 'parameters') { $gate.parameters } else { '' }
        $ratio = (Get-Mean -Type $gate.type -Method $gate.numerator -Parameters $parameters) /
                 (Get-Mean -Type $gate.type -Method $gate.denominator -Parameters $parameters)
        if ($ratio -gt $gate.maxRatio * 0.85) {
            Write-Host "  ⚠ $($gate.id): $('{0:F3}' -f $ratio) 는 임계 $($gate.maxRatio) 의 85% 를 넘었다 — 추세를 본다." -ForegroundColor Yellow
        }
    }
    exit 0
}

Write-Host ''
Write-Host "게이트 실패 — $($failures.Count) 건." -ForegroundColor Red
foreach ($f in $failures) {
    Write-Host ''
    Write-Host "  [$($f.Gate.id)]" -ForegroundColor Red
    Write-Host "    비율      : $('{0:F3}' -f $f.Ratio)  (기준 $($f.Gate.maxRatio) 이하, 명세 작성 시 $($f.Gate.baselineRatio))"
    Write-Host "    구성      : $($f.Gate.numerator) $('{0:F2}' -f $f.Numerator)ns / $($f.Gate.denominator) $('{0:F2}' -f $f.Denominator)ns"
    Write-Host "    무너진 주장: $($f.Gate.claim)"
}
Write-Host ''
Write-Host '이 게이트는 절대 시간이 아니라 비율을 본다 — 두 팔이 함께 느려진 것이 아니라' -ForegroundColor Yellow
Write-Host '한쪽의 우위가 사라진 것이다. 노이즈가 의심되면 재실행하되, 반복되면 회귀다.' -ForegroundColor Yellow
exit 1
