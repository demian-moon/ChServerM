# 레거시 자산 인벤토리 — `docs/legacy/`로 이전됨

이 문서는 **2026-07-30 초판 인벤토리**였다. 파일명·주석·부분 정독에 기반한 **추정 판정**이 다수 포함돼 있었고, 2026-07-31 전수 정독으로 여러 항목이 뒤집혔다.

**→ 현재 유효한 분석은 [`docs/legacy/`](legacy/) 다.**

| 문서 | 내용 |
|---|---|
| **[legacy/00-overview.md](legacy/00-overview.md)** | **여기부터 읽는다.** 전체 그림, 승계 자산 22종, 결함 유형 7종, 정량 근거, 정정 목록 |
| [legacy/README.md](legacy/README.md) | 파일 인덱스 — 111개 파일 → 판정 → 문서 앵커 |
| legacy/01~12 | 계층별 클래스 단위 정밀 분석 |

---

## 초판이 틀렸던 것 (전수 정독으로 확인)

이 목록 자체가 **"파일명과 주석으로 판정하면 안 된다"**는 교훈의 기록이다.

| 대상 | 초판 기재 | 실제 |
|---|---|---|
| `QuadTreeM.cs` | AOI 승계 후보 | **빈 파일.** `QuadGrid`/`LQuadTree` 타입이 코드베이스에 없다 |
| `FileWatcherSystemM` | 핫 리로드 승계 후보 | **참조 0** |
| `MemoryPoolM`, `StackMemAllocM`, `UnsafeCopyBlock` | Phase 3 버퍼 승계 후보 | **참조 0 / 전체 주석.** 실사용 풀은 `ObjectPoolM<T>` 하나 |
| 체크섬 검증 | 승계 자산 | **`return true` 한 줄.** 검증이 존재하지 않는다 |
| `HashM` | 보안·해시 계열 | **만료 지원 KV 저장소** (Redis `HSET`+`EXPIRE` 대응) |
| `ProgressBarM` | 콘솔 UI | 풀링되는 **게임 오브젝트 컴포넌트** (판정은 그대로 폐기) |
| 비밀번호 전송 | 평문 직렬화 | **AES 암호화됨.** 단 키 교환 미인증으로 MITM 노출 |
| AES 강도 | AES-256 | **AES-128** |
| 압축 정책 | "1024B 미만 무압축, LZ4" | **압축이 한 번도 실행되지 않는다.** 임계값 자체가 없다 |
| `MongoDBManagerM` | ECS 사용 3파일 중 하나 | **잉여 `using`.** ECS 실사용은 2파일 |
| `HashM` 스레드 안전성 | 안전하지 않음 | `ConcurrentDictionary` 사용. 주석은 더 오래된 경로를 가리킴 |
| `InIFileM.cs` | 참조 0 | **사용 중.** 클래스명이 `IniFileM`이라 파일명 검색이 놓쳤다 |
| `MembersM` 멤버 그룹 | 브로드캐스트 승계 후보 | **전량 주석.** 활성 코드 없음 |
| 레이팅 시스템 | ADR-0003 근거 | **참조 0.** 사용되지 않는 준비 코드 |
| ECS 사용 범위 | (인지 못 함) | 레거시는 **Arch ECS 기반**. 네임스페이스 `EcsServerLibM`의 "Ecs" |

---

## 변하지 않은 것

`LegacyServer/`와 `LagacyClient/`는 **커밋하지 않고 로컬 참조 전용**으로 둔다.
`.gitignore`에 `/LegacyServer/`, `/LagacyClient/` 등록. 각 디렉터리에 루트 `Directory.Build.props` 상속을 막는 빈 스토퍼를 배치했다(레거시는 `.net9`/`v4.8` 타깃이라 `net10.0` 설정을 상속하면 빌드가 깨진다).
