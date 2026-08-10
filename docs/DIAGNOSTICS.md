# ChServerM 진단 규칙 (CHSM)

빌드 시점에 프레임워크 규약 위반을 잡는 진단들의 정본 문서다.
대역 배정: **CHSM0xxx = 빌드/구조 가드(MSBuild)**, **CHSM1xxx = 디스패치 소스 제너레이터**,
**CHSM2xxx = 데이터 테이블 접근자 소스 제너레이터**.
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

## CHSM2xxx — 데이터 테이블 접근자 소스 제너레이터 (`ChServerM.SourceGen`)

`[StaticTableRow]` 선언에서 **스키마와 강타입 접근자를 함께** 생성할 때 보고한다(ADR-0043).

> **이 대역의 진단은 2차 방어선이다.** 이 제너레이터가 막는 1차 위험 —
> **열을 가운데에 끼워 넣으면 뒤따르는 서수가 통째로 밀리는 것** — 은 진단으로 잡을 수 없다.
> 그것은 **스키마와 접근자를 같은 선언에서 함께 만들어** 어긋날 경로 자체를 없애서 푼다.
> 여기 있는 것들은 그 선언 자체가 앞뒤가 맞는지를 지키며, 전부 **런타임이었다면 기동 시점
> 스키마 조립 예외**이거나 — 더 나쁘게는 — **조용히 무시되는 제약**이었을 것들이다.

| ID | 심각도 | 원인 | 해결 |
|---|---|---|---|
| CHSM2001 | Error | 행 타입 또는 그 바깥 타입이 `partial` 이 아님 | 행 타입과 모든 바깥 타입에 `partial` 을 붙인다 |
| CHSM2002 | Error | 키 열이 정확히 하나가 아니거나 `Optional` 로 선언됨 | `[StaticTableColumn(Key = true)]` 를 필수 열 하나에만 붙인다. **선택 키는 그 행이 키 사전에 들어가지 않아 로딩은 성공하는데 영원히 찾히지 않는다** |
| CHSM2003 | Error | 열(getter 만 있는 `partial` 인스턴스 속성)이 하나도 없음 | 열을 선언한다 |
| CHSM2004 | Error | 지원하지 않는 열 형식 | `string` · `int` · `long` · `double` · `bool` 만 쓴다. 그 밖의 형식은 로딩 시점 파싱 규약이 없다 |
| CHSM2005 | Error | 열 이름 중복, 생성 멤버와 속성 이름 충돌, 빈 테이블 이름 | 이름을 바꾼다. CSV 헤더만 맞추면 될 때는 `[StaticTableColumn(Name = "...")]` 로 분리한다 |
| CHSM2006 | Error | 선택(`Optional`) 문자열 열을 `string` 으로 선언 | `string?` 로 선언한다. 빈 칸은 `null` 로 오므로 널 비허용은 거짓말이다 |
| CHSM2007 | Error | 범위 제약이 열 종류와 맞지 않거나 뒤집힘 | 정수 열은 `MinimumInteger`/`MaximumInteger`, `double` 열은 `MinimumReal`/`MaximumReal`. **조용히 무시되는 제약은 걸지 않은 것보다 나쁘다** |
| CHSM2008 | Error | `References` 대상에 `[StaticTableRow]` 가 없거나, 참조 열이 `string` 이 아님 | 대상은 행 타입이어야 하고(거기서 표 이름을 읽는다), 참조 열은 `string` 이어야 한다 |
| CHSM2009 | — | **결번**. 원래 "`ChServerM.DataTable` 미참조" 자리였으나 어트리뷰트 자체가 그 어셈블리에 있어 도달할 수 없다 | 번호는 재사용하지 않는다(진단 ID 는 사용자 억제 설정에 박힌다) |
| CHSM2010 | Error | 행 타입이 `readonly struct` 가 아님 | `readonly partial struct` 로 선언한다. 행은 표 참조와 행 번호만 들고 다니는 값이며, 불변이어야 방어 복사가 없고 여러 파티션 워커가 동시에 들고 다녀도 안전하다 |
| CHSM2011 | Error | CSV 헤더에 선언한 열이 없음 | 헤더의 열 이름을 고치거나 `[StaticTableColumn(Name = ...)]` 를 맞춘다 |
| CHSM2012 | Error | CSV 에 헤더 줄이 없음(비었거나 주석뿐) | 헤더 줄을 넣는다 |
| CHSM2013 | Warning | CSV 헤더에 중복된 열 이름 | 리더는 **먼저 나온 열**을 쓰므로 나머지가 조용히 무시된다. 이름을 나눈다 |

### CSV 헤더 대조는 어떻게 켜지는가 (CHSM2011~2013)

**설정이 없다.** `AdditionalFiles` 로 들어온 `.csv` 중 **파일 이름(확장자 제외)이 표 이름과
같은 것**만 대조한다. 짝이 없는 CSV 는 조용히 지나가므로 이 축과 무관한 파일이 있어도 된다.

```xml
<ItemGroup>
  <AdditionalFiles Include="Tables\*.csv" />
</ItemGroup>
```

> **⚠ 여기서는 헤더만 본다.** 값 검증(타입·범위·참조 무결성·키 중복)은 **로딩 시점에 그대로
> 남는다.** 그것까지 컴파일 타임으로 옮기려면 CSV 파서와 검증기를 제너레이터 쪽
> (netstandard2.0, 런타임 축을 참조할 수 없다)에 **한 벌 더** 구현해야 하고, 두 구현이
> 갈라지면 **빌드는 통과하는데 기동은 실패**하는 최악의 조합이 나온다. 중복하는 것은
> "빈 줄과 `#` 주석을 건너뛰고 첫 줄을 헤더로 본다" 와 인용 처리뿐이다.
>
> 열 이름 오타·이름 변경은 밸런스 표에서 가장 흔한 사고이고, **그것만으로도** 에디터에서
> 줄과 함께 보이는 값이 크다.

**오류가 하나라도 있으면 그 행 타입의 접근자를 생성하지 않는다.** 반쯤 맞는 접근자를
내보내면 컴파일 오류가 먼저 눈에 들어와 진단이 가리키는 진짜 원인이 묻힌다.

## 릴리스 추적

CHSM1xxx·CHSM2xxx 의 추가·변경은 `Server/ChServerM.SourceGen/AnalyzerReleases.Unshipped.md` 에
기록한다(RS2008 게이트). 릴리스 시 Shipped 로 옮긴다 — `PublicAPI.*.txt` 와 같은 규약이다.
