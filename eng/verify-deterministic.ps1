# 결정적 빌드 검증 (Phase 21) — 같은 커밋이 같은 바이너리를 내는가.
#
# 왜 검증하는가: Deterministic=true 는 선언일 뿐이다 — GC 프로퍼티 오타가 3개월간
# 조용히 무시된 사례(ADR-0031)처럼, 결정성도 산출물로 확인해야 믿을 수 있다.
# 결정성이 깨지는 전형: 빌드 시각·절대 경로·비결정적 소스 생성기 출력.
#
# 방법: Server/ 어셈블리를 두 번 완전 재빌드(ContinuousIntegrationBuild=true 로
# 경로 정규화)하고 산출 DLL 의 SHA-256 을 비교한다.
#
# 범위: 컴파일러 산출물(DLL)까지다. nupkg 는 zip 컨테이너 메타데이터 때문에 별도
# 논의가 필요하다 — 바이트 동일 패키징은 릴리스 파이프라인에서 다룬다(ROADMAP Phase 21).

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# FlatSharp.Compiler(net9 도구)가 .NET 10 단독 환경에서 돌 수 있게 — build.ps1:52 와 동일.
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'

function Build-AndHash {
    param([string] $Label)

    Write-Host "== $Label 빌드 ==" -ForegroundColor Cyan

    # bin/obj 를 지워 완전 재빌드를 강제한다 — 증분 빌드는 결정성 검증이 아니다.
    Get-ChildItem Server -Directory | ForEach-Object {
        Remove-Item -Recurse -Force (Join-Path $_.FullName 'bin'), (Join-Path $_.FullName 'obj') -ErrorAction SilentlyContinue
    } | Out-Null

    # Out-Host 가 핵심이다 — 함수 안의 네이티브 stdout 은 반환값 스트림에 섞인다.
    dotnet build ChServerM.slnx -c Release --nologo -v q -p:ContinuousIntegrationBuild=true | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "빌드 실패" -ForegroundColor Red
        exit 1
    }

    $hashes = @{}
    # ⚠ 구분자 무관 매칭 — `\\bin\\Release\\` 는 리눅스 러너에서 하나도 안 걸려
    #   "DLL 0개 전부 동일" 이라는 거짓 통과가 된다(감사 2026-08-18 O-6 연결 작업에서 발견).
    Get-ChildItem Server -Recurse -File -Filter 'ChServerM.*.dll' |
        Where-Object { $_.FullName -match '[\\/]bin[\\/]Release[\\/]' -and $_.FullName -notmatch '[\\/]ref[\\/]' } |
        ForEach-Object {
            $relative = $_.FullName.Substring($root.Length + 1)
            $hashes[$relative] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        }

    if ($hashes.Count -eq 0) {
        # fail-closed — 검증 대상이 사라졌다면 그것 자체가 실패다(AOT 게이트와 같은 원리).
        Write-Host '검증할 DLL 이 하나도 없다 — 경로 패턴 또는 빌드 산출물을 확인한다.' -ForegroundColor Red
        exit 1
    }

    return $hashes
}

$first = Build-AndHash -Label '1차'
$second = Build-AndHash -Label '2차'

$mismatches = @()
foreach ($key in ($first.Keys | Sort-Object)) {
    if (-not $second.ContainsKey($key)) {
        $mismatches += "2차에 없음: $key"
    }
    elseif ($first[$key] -ne $second[$key]) {
        $mismatches += "해시 불일치: $key"
    }
}
foreach ($key in $second.Keys) {
    if (-not $first.ContainsKey($key)) {
        $mismatches += "1차에 없음: $key"
    }
}

Write-Host ''
if ($mismatches.Count -eq 0) {
    Write-Host "결정적 빌드 확인 — DLL $($first.Count)개 전부 두 빌드에서 동일 해시." -ForegroundColor Green
    exit 0
}

Write-Host "결정성 위반 — $($mismatches.Count)건:" -ForegroundColor Red
$mismatches | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
Write-Host '빌드 시각·절대 경로·비결정적 생성기 출력을 의심한다.' -ForegroundColor Yellow
exit 1
