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
    Get-ChildItem Server -Recurse -File -Filter 'ChServerM.*.dll' |
        Where-Object { $_.FullName -match '\\bin\\Release\\' -and $_.FullName -notmatch '\\ref\\' } |
        ForEach-Object {
            $relative = $_.FullName.Substring($root.Length + 1)
            $hashes[$relative] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
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
