# 설계 결정 기록 (ADR)

라이브러리 선택과 아키텍처 결정을 여기에 남긴다. **추가 전용** — 결정을 뒤집을 때는 기존 ADR을 수정하지 말고 새 ADR을 추가해 `Superseded by ADR-XXXX`로 표시한다.

## 작성 규칙
- 번호는 4자리 연번(`ADR-0001`)
- **대안과 탈락 이유를 반드시 적는다.** 이것이 ADR의 존재 이유다
- 성능이 근거라면 `docs/BENCHMARKS.md`의 수치를 링크한다
- 상태: `제안` / `채택` / `보류` / `폐기(Superseded by ADR-XXXX)`

## 템플릿

```markdown
## ADR-XXXX: {제목}

- **날짜**: YYYY-MM-DD
- **상태**: 채택
- **영향 범위**: {어셈블리 / 축}

### 배경
{어떤 문제를 풀어야 했는가}

### 결정
{무엇을 선택했는가}

### 대안과 탈락 이유
| 대안 | 탈락 이유 |
|---|---|
| {A} | {이유} |

### 결과
- 긍정: {얻은 것}
- 부정: {감수한 비용, 되돌리기 난이도}
```

---

## ADR-0000: 아키텍처 기본 원칙

- **날짜**: 2026-07-30
- **상태**: 채택
- **영향 범위**: 전체

### 배경
단일 서버가 아니라 기능을 조립해 서버를 구성하는 상업용 프레임워크가 목표다. 성능이 최우선이면서 동시에 구현체 교체가 가능해야 한다 — 이 둘은 보통 상충한다(추상화는 간접 호출 비용을 낳는다).

### 결정
1. `ChServerM.Core`는 서드파티 의존 0. 추상화·인터페이스·값 타입만 담는다.
2. 모든 벤더 통합은 `ChServerM.<축>.<벤더>` 어댑터 어셈블리로 격리한다.
3. 리플렉션 대신 소스 제너레이터. 전 어셈블리 `IsAotCompatible=true`.
4. 조립 비용은 **시작 시점**에 지불한다 — 빌더가 파이프라인을 델리게이트 체인으로 컴파일하고, 핫패스에는 동적 결정을 남기지 않는다.

### 대안과 탈락 이유
| 대안 | 탈락 이유 |
|---|---|
| 단일 어셈블리 + 조건부 컴파일 심볼 | 소비자가 필요 없는 벤더 의존까지 끌고 감. NuGet 배포 단위를 쪼갤 수 없다 |
| 런타임 리플렉션 기반 플러그인 로딩 | Native AOT 불가, 콜드스타트 비용, 트리밍 불가 |
| 제네릭 특수화로만 축 교체 (인터페이스 없음) | 최고 성능이지만 사용자 코드 전체에 타입 인자가 전파돼 API가 못 쓸 수준으로 복잡해진다. 향후 핫패스 국소 적용은 검토 |

### 결과
- 긍정: 축 교체가 패키지 참조 교체로 끝난다. Core만 보면 프레임워크 전체 계약을 읽을 수 있다.
- 부정: 어셈블리 수가 많아져 빌드·버전 관리 비용이 늘어난다. 축 하나를 추가할 때 Core 인터페이스부터 손대야 해서 초기 속도가 느리다.

---

## ADR-0001: raw TCP 전송 구현 방식

- **날짜**: — (2026-07-30 근거 보강)
- **상태**: 제안 (미결정, Kestrel 쪽으로 기울었음)
- **영향 범위**: `ChServerM.Transport.Tcp`

### 배경
raw TCP 서버의 소켓 계층을 직접 만들 것인지, 검증된 엔진을 재사용할 것인지.

### 후보
| 후보 | 장점 | 우려 |
|---|---|---|
| Kestrel Socket Transport (`Microsoft.AspNetCore.Connections.Abstractions`) | 프로덕션 검증된 소켓 엔진, `SocketAsyncEventArgs` 풀링 + 전용 IO 스케줄러, Pipelines 통합 완료, 유지보수 무료 | ASP.NET Core 의존 유입, 커넥션 추상화가 우리 것과 겹침 |
| 순수 `Socket` + `SocketAsyncEventArgs` + Pipelines | 완전한 제어, 최소 의존, 스레드-퍼-코어 직접 구현 가능 | 엣지 케이스(half-open, 리눅스/윈도우 차이) 전부 직접 처리 |

### 2026-07-30 추가된 근거
레거시 `LegacyServer/IoPipelineSrvM.cs`를 정독한 결과, 기존 구현은 `TcpClient.GetStream()` →
`NetworkStream.ReadAsync` 노선이다. Kestrel Socket Transport에는 이 `NetworkStream` 계층이 없다.
즉 **레거시 노선은 성능 상한이 더 낮다.** 이것이 Kestrel 재사용 쪽으로 기운 이유다.

다만 레거시 자산 승계 비용이 있으므로, Phase 5 진입 전 양쪽 프로토타입 벤치마크로 확정한다.
참고: Bedrock Framework(Kestrel 전송을 비HTTP 프로토콜에 쓰는 실험적 구현).

---

## ADR-0002: 프레임 헤더에 직렬화 포맷을 쓰지 않는다

- **날짜**: 2026-07-30
- **상태**: 채택
- **영향 범위**: `ChServerM.Core`(`IFrameDecoder`/`IFrameEncoder`), `ChServerM.Transport.*`

### 배경
레거시 `LegacyServer/FlatbufferM/PacketM.fbs`는 프레임 헤더 3종(`FbsPkHeadM`,
`FbsContentHeadM`, `FbsEncryptHeadM`)을 모두 FlatBuffers `table`로 정의했다.
스키마 주석이 문제를 자백하고 있다:

```
byteCheckSum : byte = -1;   // 0을 쓰면 안된다. 헤더 길이 달라짐
packetType : ushort;        // 디폴트 값은 저장이 안되니까(패킷 사이즈가 달라짐)해서 1부터 쓴다
conDataLen : int = -1;      // 0은 저장이 안되니까 -1로 설정 (헤더값 달라짐)
gage : ushort = 65535;      // 65535는 변경이 없다는 의미
```

원인은 명확하다. **FlatBuffers는 기본값과 같은 필드를 직렬화하지 않는다.** 그래서 헤더가
가변 길이가 되고, 고정 길이를 전제로 하는 프레이밍이 깨진다. 이를 `-1`, `65535` 같은
sentinel 값으로 우회하고 있어 값 공간이 오염되고 새 필드를 추가할 때마다 같은 함정을 밟는다.

### 결정
**프레임 헤더는 직렬화 라이브러리를 거치지 않는다.** 고정 크기 `struct` +
`MemoryMarshal`/`BinaryPrimitives`로 직접 읽고 쓴다. 헤더 크기는 컴파일 타임에 확정된다.

직렬화 라이브러리는 **페이로드에만** 적용한다. `docs/ARCHITECTURE.md`의
"프레이밍과 직렬화를 분리한다" 원칙이 정확히 이 문제를 해결한다.

### 대안과 탈락 이유
| 대안 | 탈락 이유 |
|---|---|
| FlatBuffers 헤더 + sentinel 값 유지 (레거시 방식) | 가변 길이 헤더. 값 공간 오염. 필드 추가마다 재발. 파싱 비용도 불필요하게 발생 |
| Protobuf 헤더 | 동일 문제. varint + 기본값 생략으로 길이가 가변 |
| FlatBuffers를 페이로드에서도 배제 | 과잉 대응. 역직렬화 없는 랜덤 접근은 게임 패킷에 실익이 있다. 문제는 포맷이 아니라 용도 오배치였다 |

### 결과
- 긍정: 헤더 파싱 비용 0. 프레이밍과 직렬화 축을 독립적으로 교체 가능. sentinel 규약 소멸
- 부정: 헤더 레이아웃 변경이 와이어 호환성을 직접 깬다. 버전 필드를 헤더에 미리 넣어야 한다

### 남은 미결
**페이로드 직렬화 기본값은 확정하지 않았다.** MemoryPack / FlatSharp /
Google.Protobuf·protobuf-net / MessagePack-CSharp 4자 벤치마크(Phase 6)로 결정한다.
레거시가 FlatBuffers 스키마와 생성 코드를 이미 운영 중이므로 승계 비용이 변수다.
크로스 언어 클라이언트가 요구사항에 들어오면 결론이 바뀐다.

---

## ADR-0003: 목표 워크로드 — 실시간 게임 서버

- **날짜**: 2026-07-30
- **상태**: 제안 (사용자 확인 대기)
- **영향 범위**: ROADMAP Phase 순서, `IExecutionModel`, 전송 축 우선순위

### 배경
"고성능 서버 프레임워크"만으로는 전송·동시성 모델의 우선순위를 정할 수 없다. 실시간 게임과
일반 API 서버는 요구가 다르다 — 전자는 상시 연결·틱·순서 보장, 후자는 무상태·수평 확장.

### 근거 (레거시 구성에서 도출)
| 자산 | 시사점 |
|---|---|
| `RatingSystem/GlickoM.cs`, `WengLinM.cs` | Glicko / Weng-Lin 레이팅 → 매치메이킹 |
| `QuadTreeM.cs`, `BoxColliderM.cs`, `HierachyM.cs` | 공간 분할·충돌·씬 계층 → 실시간 시뮬레이션 |
| `FbsServerTick`, `FbsLoginOk.serverFrequency` | 서버 틱 주기 동기화 |
| `NetWorkDelayM.cs` | 네트워크 지연 보정 |
| `UserM.MemPkActionBlock` | 유저별 순서 보장 |

### 제안하는 결정
목표 워크로드를 **실시간 게임 서버 + 매치메이킹**으로 확정하고, TCP 상시 연결(Phase 5)을
HTTP 무상태(Phase 16)보다 우선한다.

### 확인 필요
사용자 승인 전까지 ROADMAP Phase 순서는 변경하지 않았다. 승인되면 Phase 5/16 우선순위를
교체하고 이 ADR을 `채택`으로 올린다.
