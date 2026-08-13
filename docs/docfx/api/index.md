# API 레퍼런스

ChServerM 의 공개 API 문서다. `Server/` 아래 각 축 어셈블리의 public 타입·멤버가
소스의 XML 문서 주석에서 그대로 생성된다 — 이 프로젝트에서 문서는 제품의 일부다.

## 어디서 시작하나

왼쪽 목차에서 네임스페이스를 펼쳐 타입으로 들어간다. 자주 찾는 진입점:

- **`ChServerM.Identity`** — 강타입 ID(`MessageId`·`ConnectionId`·`ObjectId`·`SessionId`·`PartitionKey` 등).
  원시 정수를 그대로 쓰지 않는 이유가 각 타입 주석에 적혀 있다.
- **전송 축** — `IServerTransport` / `IClientTransport` / `IConnection`
- **직렬화·프레이밍 축** — `IMessageSerializer<T>`, `IFrameDecoder` / `IFrameEncoder`
- **디스패치·실행 축** — `IMessageDispatcher`, `IMessageHandler<T>`, `IExecutionModel`
- **호스팅·조립** — `ServerBuilder` / `ClientBuilder` 플루언트 API

## 범위

이 참조는 축 어셈블리의 public 표면만 담는다. 소스 생성기(`SourceGen`)·분석기
(`Analyzers`)·메타 패키지(`ChServerM`)는 제외된다 — 구현 세부이거나 참조 대상이
아니기 때문이다.

API 계약의 정본(버전 동결 판정의 기준)은 각 어셈블리의 `PublicAPI.Shipped.txt` /
`PublicAPI.Unshipped.txt` 이고, 무엇이 파괴적 변경인지는 `docs/VERSIONING.md` 를 따른다.
