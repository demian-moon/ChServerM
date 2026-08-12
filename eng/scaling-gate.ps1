<#
.SYNOPSIS
    확장성 회귀 게이트 — 코어 수 대비 처리량 배수가 하한 아래로 떨어지면 실패한다. (로컬 전용)

.DESCRIPTION
    존재 이유
    ---------
    **병렬성 퇴화는 단일 스레드 성능 회귀보다 발견이 늦다** (CLAUDE.md 9.9). 락 하나,
    공유 카운터 하나가 핫패스에 들어가면 1코어 성능은 그대로인데 16코어 배수만 무너진다.
    마이크로 벤치마크도 할당 게이트도 그것을 못 본다. 이 게이트가 유일한 장치다.

    판정 대상은 ADR-0005 의 생존 여부다 — "코어 수 대비 처리량이 선형에 근접" 하지 않으면
    키 기반 파티션 샤딩이라는 기본 전략 자체가 무효다. 그래서 임계는 "느려졌는가" 가
    아니라 **"전제가 무너졌는가"** 로 잡았다(효율 70%).

    왜 CI 에 없는가
    ---------------
    GitHub 공용 러너는 2~4 vCPU 라 곡선이 2점뿐이고 물리 코어와 SMT 형제를 구분할 수도
    없다. 거기서 도는 게이트는 의미 없는 통과를 반복해 게이트를 장식으로 만든다.
    **물리 코어가 충분한 측정 머신에서 사람이 돌린다** — 릴리스 전, 그리고 동시성 코드를
    건드린 뒤.

    ⚠ 어피니티 마스크
    -----------------
    **SMT 형제는 인접 쌍이므로 물리 코어 N 개 = 한 칸씩 건너뛴 비트다.**
    `0xF` 는 4코어가 아니라 **2코어**다. 마스크가 틀리면 곡선이 통째로 틀린다 —
    2026-08-07 에 실제로 그 오류를 발견해 바로잡았다(docs/BENCHMARKS.md).
    다른 머신에서는 `lscpu -e`(Linux) / CoreInfo(Windows)로 배치를 확인하고
    eng/scaling-gate.json 의 마스크를 고친다.

.PARAMETER MaxCores
    이 값 이하의 측정 지점만 돌린다. 코어가 적은 머신이나 빠른 확인용.
    기본값은 명세의 모든 지점.

.PARAMETER SpecPath
    게이트 명세 JSON. 기본값은 이 스크립트 옆의 scaling-gate.json.

.EXAMPLE
    ./eng/scaling-gate.ps1                # 전체 곡선 (오래 걸린다)
    ./eng/scaling-gate.ps1 -MaxCores 4    # 1·2·4 코어만 — 기구 확인용
#>
[CmdletBinding()]
param(
    [int]$MaxCores = [int]::MaxValue,
    [string]$SpecPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $SpecPath) { $SpecPath = Join-Path $PSScriptRoot 'scaling-gate.json' }
if (-not (Test-Path $SpecPath)) { throw "게이트 명세를 찾을 수 없다: $SpecPath" }

$spec = Get-Content $SpecPath -Raw -Encoding utf8 | ConvertFrom-Json
$points = $spec.points | Where-Object { $_.cores -le $MaxCores }
if (-not $points) { throw "돌릴 측정 지점이 없다 (MaxCores=$MaxCores)" }

$isWindows = $env:OS -eq 'Windows_NT'
$benchProject = Join-Path $repoRoot 'Bench/ChServerM.Bench'
$artifacts = Join-Path $repoRoot 'BenchmarkDotNet.Artifacts'
$resultsFile = Join-Path $artifacts 'results/ChServerM.Bench.Concurrency.PartitionScalingBenchmarks-report-full.json'

Write-Host '=== 확장성 회귀 게이트 (코어 수 대비 배수) ===' -ForegroundColor Cyan
Write-Host "명세: $SpecPath"
Write-Host "기준선: $($spec.baseline.measuredOn)"
Write-Host "측정 지점: $(($points | ForEach-Object { $_.cores }) -join ', ') 코어"
Write-Host "논리 프로세서(제한 전): $([Environment]::ProcessorCount)"
Write-Host ''

# FlatSharp.Compiler 는 net9.0 툴이라 .NET 9 런타임이 없는 머신에서 빌드가 깨진다.
$previousRollForward = $env:DOTNET_ROLL_FORWARD
if (-not $previousRollForward) { $env:DOTNET_ROLL_FORWARD = 'LatestMajor' }
$env:CHSM_BENCH_GATE = '1'   # 짧은 job + JSON 내보내기

# 어피니티 제한은 프로세스 밖에서 걸어야 한다 — .NET 의 ProcessorAffinity 는 Linux 에서
# 지원되지 않으므로 프로세스 안에서 코어를 줄일 수 없다(Bench/Program.cs 문서).
function Invoke-Pinned {
    param([pscustomobject]$Point)

    # 파티션 수 = 코어 수인 지점만 판정에 쓰지만, BenchmarkDotNet 은 [Params] 를 값 단위로
    # 거를 수 없으므로 전체 스윕을 돌리고 대각선만 읽는다.
    #
    # --buildTimeout: 어피니티는 프로세스 트리 전체(빌드 포함)를 지정 코어에 묶는다.
    # 1코어 지점(0x1)에서는 BenchmarkDotNet 이 격리 ArtifactsPath 에 의존 그래프를 통째로
    # 클린 빌드(18개 프로젝트 + net9 FlatSharp 코드젠)하는데, 솔루션이 커지면서 이 빌드가
    # BDN 기본 빌드 타임아웃 2분을 넘겨 측정이 0건으로 죽었다(2026-08-12 실측). 판정 대상은
    # 런타임 확장성이지 빌드 시간이 아니므로 빌드에만 넉넉한 상한을 준다. 이 상한은
    # 어피니티로 빌드가 1코어에 갇히는 이 스크립트에서만 필요하다(CI bench-gate 는 미핀).
    $benchArgs = @('run', '-c', 'Release', '--project', $benchProject, '--', '--filter', '*PartitionScaling*', '--buildTimeout', '900')

    if ($isWindows) {
        # start 는 빈 창 제목이 필요하다. /wait 로 동기 실행, /b 로 새 창을 열지 않는다.
        $inner = "dotnet $($benchArgs -join ' ')"
        & cmd /c "start `"`" /affinity $($Point.windowsMask) /wait /b $inner"
    }
    else {
        & taskset -c $Point.linuxCpus dotnet @benchArgs
    }

    if ($LASTEXITCODE -ne 0) { throw "$($Point.cores) 코어 측정이 실패했다 (exit $LASTEXITCODE)" }
}

function Get-DiagonalMean {
    param([int]$Cores)

    if (-not (Test-Path $resultsFile)) { throw "확장성 결과 JSON 이 없다: $resultsFile" }
    $report = Get-Content $resultsFile -Raw -Encoding utf8 | ConvertFrom-Json

    $match = $report.Benchmarks | Where-Object { $_.Parameters -eq "PartitionCount=$Cores" }
    if (-not $match) {
        $available = ($report.Benchmarks | ForEach-Object { $_.Parameters }) -join ', '
        throw "파티션 $Cores 지점을 찾을 수 없다. 측정된 값: $available`n  ([Params] 목록에 코어 수와 같은 값이 있어야 한다)"
    }

    # ns → ms
    return [double]$match.Statistics.Mean / 1e6
}

$measured = @()
try {
    foreach ($point in $points) {
        $mask = if ($isWindows) { "0x$($point.windowsMask)" } else { $point.linuxCpus }
        Write-Host "[$($point.cores) 코어] 어피니티 $mask 로 측정 중..." -ForegroundColor Yellow

        if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
        Invoke-Pinned -Point $point

        $ms = Get-DiagonalMean -Cores $point.cores
        $measured += [pscustomobject]@{ Point = $point; Ms = $ms }
        Write-Host "  → $('{0:F2}' -f $ms) ms (기준선 $($point.baselineMs) ms)" -ForegroundColor DarkGray
    }
}
finally {
    Remove-Item Env:\CHSM_BENCH_GATE -ErrorAction SilentlyContinue
    if (-not $previousRollForward) { Remove-Item Env:\DOTNET_ROLL_FORWARD -ErrorAction SilentlyContinue }
}

# --- 판정 -------------------------------------------------------------------
# 배수는 이 실행의 1코어 값을 기준으로 계산한다. 기준선 ms 를 기준으로 삼으면 머신이
# 다를 때 전 지점이 함께 틀어져 판정이 무의미해진다 — 확장성은 절대 속도가 아니라
# **같은 머신 안에서의 비율**이다.
$single = $measured | Where-Object { $_.Point.cores -eq 1 }
if (-not $single) { throw "1 코어 지점이 없다 — 배수를 계산할 기준이 없다." }
$singleMs = $single.Ms

$rows = @()
$failures = @()
foreach ($m in $measured) {
    $speedup = $singleMs / $m.Ms
    $efficiency = $speedup / $m.Point.cores * 100
    $passed = $speedup -ge $m.Point.minSpeedup

    $rows += [pscustomobject]@{
        Cores      = $m.Point.cores
        Mean       = '{0:F2} ms' -f $m.Ms
        Speedup    = '{0:F2}x' -f $speedup
        Min        = '{0:F2}x' -f $m.Point.minSpeedup
        Efficiency = '{0:F1}%' -f $efficiency
        Baseline   = '{0:F2}x' -f $m.Point.baselineSpeedup
        Result     = if ($passed) { 'PASS' } else { 'FAIL' }
    }

    if (-not $passed) { $failures += [pscustomobject]@{ Point = $m.Point; Speedup = $speedup; Efficiency = $efficiency } }
}

Write-Host ''
$rows | Format-Table -AutoSize | Out-String | Write-Host

if ($failures.Count -eq 0) {
    Write-Host "게이트 통과 — $($measured.Count) 개 지점 전부 하한 이상." -ForegroundColor Green
    if ($MaxCores -lt [int]::MaxValue) {
        Write-Host "⚠ -MaxCores $MaxCores 로 곡선을 잘라 돌렸다. 릴리스 판정은 전체 곡선으로 한다." -ForegroundColor Yellow
    }
    exit 0
}

Write-Host "게이트 실패 — $($failures.Count) 개 지점." -ForegroundColor Red
foreach ($f in $failures) {
    Write-Host ''
    Write-Host "  [$($f.Point.cores) 코어] 배수 $('{0:F2}' -f $f.Speedup)x (하한 $($f.Point.minSpeedup)x, 기준선 $($f.Point.baselineSpeedup)x) · 효율 $('{0:F1}' -f $f.Efficiency)%" -ForegroundColor Red
}
Write-Host ''
Write-Host 'ADR-0005 는 스스로 무효 조건을 달아뒀다 — 이 곡선이 선형에 근접하지 않으면' -ForegroundColor Yellow
Write-Host '키 기반 파티션 샤딩이라는 기본 전략 자체가 무효다. 이 실패는 "조금 느려졌다" 가' -ForegroundColor Yellow
Write-Host '아니라 "공유 상태가 들어왔다" 를 뜻한다 — 최근 동시성 변경부터 본다.' -ForegroundColor Yellow
exit 1
