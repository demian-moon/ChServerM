# 09 — 관측 (로깅 / 통계)

**전량 정독 완료** — `PublicLib/Logger/LogM.cs`(165), `BasicLibM/Log4Net/TcpLogRecieverM.cs`(248), `PublicUtil/StatisticsM.cs`(151) — 총 564줄

---

## 🔴 이 계층의 근본 문제 — 로그 레벨이 없다

```csharp
public abstract class AbLogM<T>
{
    abstract public void WriteAsync(T msg);
    abstract public void FlushLogs();
    abstract public void Debug(T msg);
}
```

**추상화에 로그 레벨이 존재하지 않는다.** `Debug` 하나뿐이고, `WriteAsync`조차 내부에서 `logM.Debug(msg)`를 호출한다.

결과:
- 오류·경고·정보를 **구분할 수 없다**
- 프로덕션에서 로그 레벨로 필터링할 수 없다 — 전부 Debug이거나 전부 꺼지거나
- 코드 전반에서 `System.Diagnostics.Debug.WriteLine`(Release에서 소멸)과 `logM.Debug`가 **혼용**된다(문서 01~08에서 반복 지적)

이것이 레거시에 "치명적 버그를 로그로 발견하지 못한" 구조적 이유다. 문서 01~08에서 찾은 버그 대부분이 **조용히 실패**하는 종류인 것과 직결된다.

---

## `AbLogM<T>` / `Log4NetM`

`PublicLib/Logger/LogM.cs:20`, `:31`

### 동작

log4net 래퍼. 생성자 2종:
- `Log4NetM(loggerName, configFileName, ipUdpAppender)` — 서버용
- `Log4NetM(loggerName, unityStreamingAssetPath)` — Unity 클라이언트용

**UdpAppender 동적 IP 설정** (`:50~66`) — 설정 파일의 `UdpAppender`를 찾아 `RemoteAddress`를 런타임에 지정하고 `ActivateOptions()`로 반영한다.

> **중앙 로그 수집 의도는 타당하다.** 다수 서버 인스턴스의 로그를 UDP로 한 곳에 모으는 구조다. `TcpLogRecieverM`(아래)이 그 수신 측이다.

`FlushLogs()` — `BufferingAppenderSkeleton`을 찾아 `Flush()`. 종료 시 버퍼 유실 방지.

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 🔴 **로그 레벨 부재** (위 참조) | `:20~28` | 🔴 치명 |
| 2 | 🔴 **설정 파일이 없으면 로깅이 통째로 조용히 비활성화된다.** `File.Exists == false` → `return` → `logM`이 null → 모든 메서드가 null 체크 후 무반응. 알림은 `System.Diagnostics.Debug.WriteLine` 뿐이라 **Release에서는 아무 흔적도 남지 않는다** | `:41~45`, `:95`, `:113`, `:123` | 🔴 치명 |
| 3 | **`WriteAsync`가 비동기가 아니다.** `logM.Debug(msg)`를 동기 호출한다. 이름이 거짓 | `:121~137` | 🟠 중간 |
| 4 | Unity 생성자의 `#if UNITY_IOS \|\| UNITY_ANDROID return;` — **생성자에서 조기 반환**해 모바일에서는 로깅이 항상 비활성 | `:74~76` | 🟠 중간 |
| 5 | `LoadLoggerForUnity`가 **빈 메서드** | `:139~142` | 🟡 낮음 |
| 6 | 문자열 보간으로 로그를 만든다 — 레벨이 꺼져 있어도 **문자열이 항상 생성**된다. 무할당 로깅(ZLogger)과 정반대 | 전역 | 🟠 중간 |
| 7 | 구조적 로깅 없음 — 필드가 아니라 문자열 한 덩어리라 검색·집계가 어렵다 | 전역 | 🟠 중간 |
| 8 | 상관관계 ID(요청·세션 추적)가 없다 | 전역 | 🟠 중간 |

### `FileLogM` (`:147`)

```csharp
public class FileLogM : IDisposable {
    FileM _fileM;
    public FileLogM(string filePath) { _fileM = new FileM(filePath); }
    async Task Write(string contents) { ... }   // private, 호출하는 곳 없음
    public void Dispose() { _fileM.Dispose(); }
}
```

**`Write`가 `private`이고 아무도 호출하지 않는다.** 생성자와 `Dispose`만 동작하는 껍데기다.

### 판정

🔴 **폐기**. ChServerM은 **ZLogger**(무할당 구조적 로깅)로 간다. 승계할 것은 **UdpAppender 중앙 수집 발상**뿐이며, 그것도 OpenTelemetry OTLP 익스포터로 대체된다.

→ Phase 11

---

## `UdpLogReceiverM` / `TcpLogRecieverM`

`BasicLibM/Log4Net/TcpLogRecieverM.cs:16`, `:114`

### 동작

로그 **수신 서버**. `Log4NetM`의 UdpAppender가 보낸 로그를 받는 쪽이다.

**`UdpLogReceiverM`** (`:16`) — `UdpClient(port)`로 무한 수신 루프. `event Func<string, ValueTask> LogMessageReceived`로 전달
**`TcpLogRecieverM`** (`:114`) — `TcpListener`로 커넥션을 받고 `StreamReader.ReadLineAsync()`로 줄 단위 수신. `event LogReceivedEventHandler LogReceived`
**`AppenderSkeleton` 파생** (`:212`) — log4net 커스텀 어펜더

> **중앙 로그 수집 인프라를 직접 만들었다.** 발상은 옳지만 2025년 기준으로는 **OpenTelemetry + OTLP 수집기**가 표준이고, 이미 검증된 구현이 있다.

### 문제점

| # | 문제 | 심각도 |
|---|---|---|
| 1 | **UDP 로그는 유실된다.** 순서 보장도 없다. 장애 조사용 로그가 정작 장애 시(네트워크 혼잡) 사라진다 | 🔴 높음 |
| 2 | **인증·암호화 없음.** 누구나 로그 포트에 임의 데이터를 주입할 수 있다 | 🔴 높음 |
| 3 | `while (true)` 수신 루프에 종료 경로가 불명확 | 🟠 중간 |
| 4 | 백프레셔 없음 — 수신 속도가 처리 속도를 넘으면 메모리 증가 | 🟠 중간 |
| 5 | 자체 구축이라 **유지보수 부담**을 떠안는다 | 🟠 중간 |

### 판정

🔵 **참고**. 중앙 수집 요구는 유효하나 구현은 폐기. → Phase 11 (OpenTelemetry + OTLP)

---

## `InterQuartileM<T>` — 🟢 IQR 이상치 제거

`PublicUtil/StatisticsM.cs:13`

### 동작

네트워크 지연 통계의 핵심. `NetWorkDelayM`(문서 01)이 사용한다.

**`RemoveOutliersAndAverage<T>(T[] sortedArray, int arrCnt)`** (`:48`)
1. `arrCnt < 4`면 단순 평균
2. `q1Idx = arrCnt >> 2`, `q3Idx = q1Idx * 3`으로 사분위 인덱스
3. `IQR = q3 - q1`, 경계 = `q1 - 1.5·IQR` ~ `q3 + 1.5·IQR`
4. **정렬된 배열이므로 양 끝에서 경계까지 인덱스를 좁혀** 유효 구간을 찾는다
5. 유효 구간의 평균 반환

> **정렬 상태를 이용해 필터링을 O(이상치 개수)로 끝낸다.** LINQ `Where`로 전체를 훑지 않는다. 무할당 경로를 의식한 설계다.
>
> 네트워크 지연은 스파이크가 흔해 단순 평균이 쓸모없다. **IQR로 이상치를 잘라내는 접근은 옳다.**

**`RemoveOutliers(List<T>)`** (`:112`) — 더 정확한 버전. `GetQuantile`이 **선형 보간**으로 사분위를 계산한다. 대신 LINQ + 새 리스트 할당

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | 🔴 **`IConvertible.ToDouble(null)`이 원소마다 박싱을 유발한다.** 제네릭 제약이 `IConvertible`이므로 값 타입이 인터페이스로 변환되며 **힙 할당**이 발생한다. 매 서버 틱 × 유저 수만큼 호출되는 핫패스에서 치명적 | `:60`, `:68~69`, `:80`, `:84`, `:94` | 🔴 치명 |
| 2 | **제네릭 파라미터 섀도잉.** 클래스가 `InterQuartileM<T>`인데 메서드가 **자기 `T`를 다시 선언**한다(`RemoveOutliersAndAverage<T>`). CS0693 경고이고 읽는 사람을 혼란시킨다 | `:13`, `:48` | 🟠 중간 |
| 3 | **작은 배열에서 사분위 계산이 부정확하다.** `q3Idx = (arrCnt >> 2) * 3` — `arrCnt = 7`이면 q1Idx=1, q3Idx=3인데 올바른 Q3 위치는 약 5다 | `:66~67` | 🟠 중간 |
| 4 | **빈 `finally { }` 블록** | `:101~104` | 🟡 낮음 |
| 5 | 정확도가 다른 구현이 둘 (`RemoveOutliersAndAverage` 근사 vs `RemoveOutliers` 보간) — 언제 무엇을 쓸지 문서화되지 않음 | `:48`, `:112` | 🟡 낮음 |
| 6 | `RemoveOutliers`가 LINQ + `ToList()`로 새 리스트 할당 | `:127` | 🟠 중간 |
| 7 | 클래스 `T` 제약은 `struct`인데 메서드 `T`에는 `struct`가 없다 | `:13`, `:49` | 🟡 낮음 |
| 8 | 주석 처리된 이전 버전이 24줄 | `:15~38` | 🟡 낮음 |

### 개선점 (ChServerM)

- **IQR 알고리즘은 승계.** 다만 `IConvertible` 제네릭을 버리고 **`long`/`double` 전용 오버로드** 또는 .NET 7+ **제네릭 수학(`INumber<T>`)** 으로 재작성 → **박싱 제거**
- `Span<T>` 입력으로 무할당
- 사분위 인덱스를 **표준 방식**(nearest-rank 또는 선형 보간)으로 통일
- 지연 통계는 Phase 11 메트릭(히스토그램)과 통합 — OpenTelemetry의 히스토그램이 p50/p99를 직접 제공하므로 **직접 계산이 불필요해질 수도 있다.** 먼저 검토

### 판정

🟢 **승계** (알고리즘) / 🟡 **개작** (구현). → Phase 11 (메트릭), Phase 17 (지연 추정)

---

## 이 계층의 종합

| 항목 | 판정 | Phase |
|---|---|---|
| **IQR 이상치 제거 + 정렬 활용 필터링** | 🟢 승계 | 11·17 |
| **중앙 로그 수집 요구** | 🔵 참고 | 11 |
| **종료 시 버퍼 Flush** | 🔵 참고 | 11 |
| `AbLogM` 추상화 (레벨 없음) | 🔴 폐기 | 11 |
| log4net | 🔴 폐기 (→ ZLogger) | 11 |
| 자체 UDP/TCP 로그 수신기 | 🔴 폐기 (→ OTLP) | 11 |
| `FileLogM` | 🔴 폐기 (껍데기) | — |

### 새 코드에 절대 옮기면 안 되는 것

1. `LogM.cs:20~28` — **로그 레벨 없는 추상화**
2. `LogM.cs:41~45` — **설정 파일 부재 시 로깅이 조용히 전면 비활성화**
3. `LogM.cs:121` — **`WriteAsync`가 동기 실행** (이름이 거짓)
4. `StatisticsM.cs:60~94` — **`IConvertible.ToDouble()` 박싱을 핫패스에서**
5. `StatisticsM.cs:13,48` — **제네릭 파라미터 섀도잉** (CS0693)
6. UDP 로그 전송 — **유실·비인증**

### Phase 11 설계에 반영할 것

이 계층의 상태가 **문서 01~08에서 발견한 버그들이 왜 오래 살아남았는지**를 설명한다.

- 로그 레벨이 없어 **오류를 구분·필터링할 수 없었다**
- 설정 파일 하나 없으면 **로깅이 통째로 사라지는데 그 사실조차 알 수 없었다**
- `Debug.WriteLine`이 Release에서 소멸해 **프로덕션에는 아무 기록이 없었다**
- 메트릭이 없어 **"조용히 실패"하는 코드**(압축 미실행, 재시도 무효, 만료 미동작)를 탐지할 방법이 없었다

→ Phase 11은 **로깅뿐 아니라 "실패가 관측되는가"를 설계 목표**로 삼는다.
구체적으로: 조용한 실패가 가능한 지점마다 **카운터 메트릭**을 두고(드롭된 패킷, 미반납 풀 대여, 실패한 재시도, 만료되지 않은 잡), 0이 아니면 경보한다.
