# 버전 정책 — 무엇이 파괴적 변경인가

이 문서가 버전 판정의 정본이다. 근거와 대안은 [ADR-0069](DECISIONS.md)에 있다.

## 한 줄 요약

- **전 패키지 락스텝** — `ChServerM.*` 전부가 같은 버전으로 함께 릴리스된다.
  정본은 `Directory.Build.props` 의 `VersionPrefix` 하나다.
- **지금은 0.x** — 파괴적 변경은 minor 승격 + 릴리스 노트 명기, patch 는 언제나 비파괴.
- **1.0 = 공개 표면 동결** — `PublicAPI.Unshipped → Shipped` 전량 이동과 동시에 선언하고,
  그 뒤부터 아래 표가 강제 규약이 된다.

## 계약 표면 5개와 판정표 (1.0 이후)

버전을 올리기 전에 다섯 표면을 전부 본다. **하나라도 major 면 major 다.**

| 표면 | major (파괴) | minor (추가) | patch |
|---|---|---|---|
| **코드 API** | `PublicAPI.Shipped.txt` 에서 줄 제거·시그니처 변경. Core 축 인터페이스에 멤버 추가(구현자 파괴) | public 타입·멤버 추가 (`Unshipped` 에 새 줄) | 표면 변화 없음 |
| **와이어** | 프레임 헤더·버전 협상 동결 레이아웃·세션 저장 형식 변경 | 프로토콜 버전 구간 확장(협상 축이 흡수) | — |
| **동작** | 옵션 기본값 변경 · 실행 순서/수명 계약 변경 | 새 옵션 추가(기본값 = 기존 동작 유지) | 버그 수정(문서화된 계약대로 되돌리는 것) |
| **관측** | 메트릭 이름·`EventId`·진단 ID 의 제거·의미 변경 | 이름·ID 추가 | — |
| **분석기** | 진단 ID 제거 후 다른 의미로 재사용 | 새 진단 추가 · 기본 심각도 상향(노트 명기) | 메시지 문구 개선 |

**비계약(자유 변경)**: 예외·로그의 문구, `internal` 전부, `Bench/`·`Samples/`·`Templates/`.
예외의 **타입**은 코드 API 계약이다. ID·이름의 결번은 재사용하지 않는다 — 억제 설정과
대시보드에 박혀 있다(CHSM2009 규약).

## 자주 틀리는 판정

- **"인터페이스에 멤버 하나 추가했을 뿐"** — Core 축 인터페이스는 사용자가 구현하는
  계약이다. 멤버 추가 = 기존 구현체 컴파일 실패 = **major**. 기본 인터페이스 구현(DIM)으로
  우회하지 않는다(명시성·AOT 원칙). 추가가 잦을 것 같으면 인터페이스가 아니라 옵션·기능
  컬렉션(`IFeatureCollection`)이 맞는 자리인지 먼저 의심한다.
- **"기본값만 살짝 바꿨다"** — 조용한 동작 변화가 최악의 파괴다(선언과 산출물이 3개월
  어긋난 GC 사례, ADR-0031). 기본값 변경 = **major**. 새 동작을 원하면 옵션을 추가하고
  기본값은 기존 동작으로 둔다.
- **"메트릭 이름을 더 좋은 이름으로"** — 경보·대시보드가 그 이름을 소비한다. 새 이름을
  추가(minor)하고 옛 이름을 한 릴리스 동안 병행 발행한 뒤 다음 major 에서 제거한다.

## 지원 정책 (요약)

정본은 루트 [SECURITY.md](../SECURITY.md), 근거는 ADR-0072. 요약:
**최신 minor 가 전부를 받고, 직전 minor 는 다음 minor 출시 후 6개월간 보안
패치만 받는다**(patch 백포트). 0.x 동안은 최신 릴리스만 지원하며 정식 효력은
1.0 부터다. 이 정책이 성립하는 전제가 위 판정표다 — minor 가 비파괴여야
"최신 minor 로 올리라"는 안내가 무리한 요구가 되지 않는다.

## 릴리스 절차 (요약)

1. 표면 점검 — 위 판정표 5행. diff 근거: `PublicAPI.*.txt`(RS0016/0017) ·
   `AnalyzerReleases.*.md`(RS2008) · 와이어 동결 테스트.
2. 버전 결정 — `Directory.Build.props` 의 `VersionPrefix` 갱신(락스텝).
3. `Unshipped → Shipped` 이동(코드 API·분석기 각각) — 이 diff 가 릴리스 노트의 뼈대다.
   **(1.0 이후에만.)** 0.x 동안은 이동하지 않고 전량 `Unshipped` 에 둔다 — 이동은 1.0 선언과
   동시에 1회 수행한다(위 "한 줄 요약"). 0.x 의 호환성 검출은 PackageValidation
   기준선(현재 0.1.0)이 담당한다.
4. 태그(`v{버전}`) 푸시 — `.github/workflows/release.yml` 이 게이트 재검증 →
   pack(CIB) → 출처 증명 → (활성화 후) NuGet 발행까지 수행한다.
   릴리스 노트는 `eng/release-notes.ps1` 로 생성한다.
5. 릴리스 직전 수동 게이트 — `eng/scaling-gate.ps1`(확장성 곡선, 조용한 측정 머신) ·
   24h soak(`CHSM_SOAK_SECONDS=86400`).

API 호환성 자동 검사는 **2026-08-12 활성화됐다** — `PackageValidationBaselineVersion`
(현재 0.1.0) + `eng/build.ps1` 의 pack 단계가 CI 마다 기준선 대비 호환성을 검사한다.
절차 1번(표면 점검)은 여전히 사람이 한다 — 게이트는 검출 장치이지 판정의 대체가 아니다.

## 첫 발행 전 1회 작업 — 저장소 공개 전환 체크리스트

발행(공개 피드)과 신고 채널·출처 증명은 저장소 공개를 전제한다. 전환 시점에
한 번만 수행한다:

1. **(사용자)** 저장소를 공개로 전환 — `README.md`·`LICENSE`·`NOTICE`·
   `SECURITY.md`·`THIRD-PARTY-NOTICES.md` 가 전면에 노출된다(전부 준비됨)
   ✅ 2026-08-12 완료 (레거시 자격증명 노출 확인 후 사용자 승인)
2. **(사용자)** Security 탭에서 **Private Vulnerability Reporting 활성화** —
   `SECURITY.md` 가 안내하는 신고 채널이 이때 실제로 열린다 ✅ 2026-08-12 완료
3. 출처 증명은 자동 활성화된다 — `release.yml` 의 공개 저장소 조건 가드
4. **(사용자)** 발행 인증은 **Trusted Publishing** — 장수명 API 키를 쓰지 않는다.
   nuget.org 로그인 → 프로필 → Trusted Publishing → 정책 추가:
   Repository Owner `demian-moon` · Repository `ChServerM` · Workflow File
   `release.yml`(파일명만) · Environment 비움. 정책 소유자는 패키지를 소유할 계정
   ✅ 2026-08-12 완료 (⚠ 사용자명은 정책 생성자 — 필드 불일치는 401 로 드러난다, ADR-0073)
5. **(사용자)** nuget.org 프로필 이름을 시크릿으로 등록:
   `gh secret set NUGET_USER --repo demian-moon/ChServerM --body "<프로필명>"`
   (비밀 값은 아니지만 공개 워크플로 파일에 박지 않기 위한 조치) ✅ 2026-08-12 완료
6. `v0.1.0` 태그 푸시 → 게이트 → pack → 증명 → 발행(태그에서만 — 리허설은
   발행 직전에 멈춘다). ✅ 2026-08-12 첫 발행 완료(33개 + 심볼 30개)
   ⚠ **출처 증명의 검증 대상은 워크플로 아티팩트(`packages-v{태그}`)다** —
   nuget.org 는 업로드본에 저장소 서명을 덧붙여 다운로드본의 해시가 달라진다
   (v0.1.0 실측: 업로드 9FEC… ↔ 다운로드 1876…). 검증 명령:
   `gh attestation verify <아티팩트의 nupkg> --repo demian-moon/ChServerM`.
   nuget.org 다운로드본 자체는 `dotnet nuget verify`(저장소 서명)로 확인한다
7. 템플릿(Phase 20)을 ProjectReference → PackageReference 로 전환 · API 호환성
   CI(PackageValidation baseline = 이 첫 패키지) 활성화
   ✅ 2026-08-12 완료 (`c2c7464` 템플릿 · `b69581c` 기준선 + pack 단계)
