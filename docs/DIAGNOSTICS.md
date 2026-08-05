# ChServerM 진단 규칙 (CHSM)

빌드 시점에 프레임워크 규약 위반을 잡는 진단들의 정본 문서다.
대역 배정: **CHSM0xxx = 빌드/구조 가드(MSBuild)**, **CHSM1xxx = 디스패치 소스 제너레이터**.
새 대역이 필요하면 여기에 먼저 배정을 기록한다.

## CHSM0xxx — 빌드/구조 가드

| ID | 심각도 | 내용 |
|---|---|---|
| CHSM0001 | Error | `ChServerM.Core` 에 서드파티 `PackageReference`/`ProjectReference` 유입. Core 무의존 하드 룰의 MSBuild 쪽 가드다(다른 한쪽은 `CoreDependencyTests`). 분석기처럼 소비자에게 전파되지 않는 패키지는 `PrivateAssets="all"` 로 명시하면 통과한다 |

## CHSM1xxx — 디스패치 소스 제너레이터 (`ChServerM.SourceGen`)

`[MessageHandler]` 선언을 검증하고 등록 코드를 생성할 때 보고한다.
런타임 조립 예외로만 드러나던 실패를 컴파일 타임으로 당기는 것이 목적이므로,
낮춰서는 안 되는 진단(Error)을 `.editorconfig` 로 끄지 않는다.

| ID | 심각도 | 원인 | 해결 |
|---|---|---|---|
| CHSM1001 | Error | 같은 메시지 ID 에 핸들러가 둘 이상 (해당 ID 의 모든 선언 위치에 보고) | ID 를 나누거나 핸들러를 합친다 |
| CHSM1002 | Error | `[MessageHandler]` 가 붙은 타입이 `IMessageHandler<TMessage>` 를 구현하지 않음 | 계약을 구현하거나 어트리뷰트를 제거한다 |
| CHSM1003 | Error | 메시지 ID 0 사용 — 0 은 '설정되지 않음' 센티넬이다(`MessageId.None`) | 1 이상을 쓴다 |
| CHSM1004 | Error | `IMessageHandler<TMessage>` 를 여러 메시지 타입으로 구현해 등록 대상이 모호 | 핸들러를 메시지 타입별로 나눈다 |
| CHSM1005 | Warning | 프레임워크 예약 대역(40001~65535) ID 사용 | 앱 메시지는 1~40000(`MessageId.AppRangeEnd`)을 쓴다. 프레임워크 내부 핸들러만 이 대역이 정당하다 |
| CHSM1006 | Error | 추상 클래스 또는 제네릭 정의에 `[MessageHandler]` | 구체(closed) 타입에만 붙인다 |
| CHSM1007 | Warning | 핸들러는 발견했지만 어셈블리가 `ChServerM.Hosting` 을 참조하지 않아 등록 코드를 생성하지 않음 | 핸들러 전용 라이브러리라면 정상이다(검증 진단은 그대로 적용된다). 등록 코드가 필요하면 Hosting 을 참조한다 |

## 릴리스 추적

CHSM1xxx 의 추가·변경은 `Server/ChServerM.SourceGen/AnalyzerReleases.Unshipped.md` 에
기록한다(RS2008 게이트). 릴리스 시 Shipped 로 옮긴다 — `PublicAPI.*.txt` 와 같은 규약이다.
