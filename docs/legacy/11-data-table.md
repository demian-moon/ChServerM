# 11 — 데이터 테이블 / 파일 / 설정

**정독 완료** — `Table/SrvTableM.cs`(76)
**구조 파악 + 참조 분석** — `PublicLib/FileM/MetaDataM.cs`(740), `FileM/InIFileM.cs`(1073), `FileM/FileM.cs`(349), `FileM/StringAnalyzerM.cs`(193), `FileM/LoadableDataInStructM.cs`(170), `FileM/FileWatcherSystemM.cs`(84), `PublicLib/IniOptionM.cs`(172), `BasicLibM/CsvParser.cs`(182), `BasicLibM/ExcelLibM/ExcelLibM.cs`(2166), `ExcelLibM/ExcelODBCM.cs`(927)

---

## 🔴 참조 분석 — 절반이 죽은 코드다

전수 검색으로 실제 사용 여부를 확인했다.

| 파일 | 줄수 | 참조 | 판정 |
|---|---:|---:|---|
| `MetaDataM.cs` | 740 | **4** | 🟡 개작 — 데이터 테이블의 핵심 |
| `InIFileM.cs` (클래스명 `IniFileM`) | 1073 | **사용 중** | 🔴 폐기 |
| `FileM.cs` | 349 | **3** | 🔵 참고 |
| `StringAnalyzerM.cs` | 193 | **1** | 🔵 참고 |
| `LoadableDataInStructM.cs` | 170 | **3** | 🟢 승계 (개념) |
| `IniOptionM.cs` | 172 | 사용 중 | 🔴 폐기 |
| `SrvTableM.cs` | 76 | **2** | 🟡 개작 |
| **`ExcelLibM.cs`** | **2166** | **0** | 🔴 **폐기 (미사용)** |
| **`ExcelODBCM.cs`** | **927** | **0** | 🔴 **폐기 (미사용)** |
| **`CsvParser.cs`** | **182** | **0** | 🔴 **폐기 (미사용)** |
| **`FileWatcherSystemM.cs`** | **84** | **0** | 🔴 **폐기 (미사용)** |

**미참조 3,359줄** — 이 계층 5,332줄의 **63%가 어디서도 호출되지 않는다.**

> **이전 판정 정정 2건**
> 1. `InIFileM.cs`를 "참조 0"으로 집계했으나 **틀렸다.** 파일명은 `InIFileM.cs`인데 **클래스명은 `IniFileM`**이라 파일명 기준 검색이 놓쳤다. `IniOptionM`이 실제로 사용한다
> 2. ROADMAP Phase 14와 `LEGACY-INVENTORY.md`에 **`FileWatcherSystemM`을 "핫 리로드 승계 후보"로 기재**했으나, **어디서도 참조되지 않는다.** 핫 리로드는 승계할 구현이 없다 — Phase 14에서 처음부터 설계해야 한다 (문서 03의 QuadGrid와 같은 상황)

---

## `MetaDataM` — 데이터 테이블의 핵심

`PublicLib/FileM/MetaDataM.cs:51`

### 동작

**행/열 문자열 테이블 + 인덱싱**을 제공하는 범용 데이터 테이블.

| 기능 | 메서드 |
|---|---|
| 로딩 | `GetMetaDataFromFileAsync(path)`, `GetMetaDataFromFile`, `GetMetaDataFromString`, `GetMetaDataFromExcel` |
| 조회 | `GetData(key, colIdx)`, `GetDataInteger(key, colIdx)`, `DataExist(key)`, `TryGetHeaderIdx` |
| 인덱스 | `ConvertToIndexMeta()`, `TryGetLineKeyWithIndexKey`, `TryGetIndexKeyWithLineKey`, `InsertIndexCol` |
| 참조 | `ConvertColToIndexRefMetaM(col, refMeta)` — **다른 테이블을 참조하는 열을 인덱스로 변환** |
| 직렬화 | `MetaDataM(FbsMetaData)` — FlatBuffers로 클라에 전송 (문서 02 `FsMetaDataFactory`) |

**`MetaDataRuntimeTableMan<T>`** (`:21`, `where T : LoadableDataInStructM, new()`)
`GetTableRunTime(strKeyLine)` — 행 키로 **강타입 구조체**를 얻는다. 문자열 테이블을 타입 있는 객체로 매핑하는 계층.

`SrvGlobal.SetSrvGloalVariable`(문서 01)이 `srvTable.serverConfig.GetDataInteger("srvUpdateDeltaMs", 1)` 형태로 이것을 소비한다.

> **설계 의도가 명확하다.**
> - `.smt` 파일 → `MetaDataM`(문자열 테이블) → `MetaDataRuntimeTableMan<T>`(강타입) 3단
> - **테이블 간 참조를 인덱스로 변환**(`ConvertColToIndexRefMetaM`)해 조회를 O(1)로
> - **같은 테이블을 FlatBuffers로 클라에 전송**해 서버·클라가 동일 데이터를 공유
>
> 마지막 항목이 특히 좋다 — 밸런스 테이블을 서버가 로딩하고 클라에 그대로 내려보내면 **불일치가 원천 차단**된다.

### 문제점

| # | 문제 | 심각도 |
|---|---|---|
| 1 | **모든 값이 `string`.** 숫자를 쓰려면 `GetDataInteger`가 매번 `int.Parse`를 한다 — 조회마다 파싱 비용 + 컬처 의존 | 🟠 중간 |
| 2 | **로딩 시 검증이 없다.** 참조 무결성·범위·타입을 확인하지 않는다. 잘못된 값은 첫 조회 시점에 예외가 되거나 조용히 기본값이 된다 | 🔴 높음 |
| 3 | `SrvGlobal.SetSrvGloalVariable`이 값 누락을 **`Debug.Assert`로 검사**(문서 01) — Release에서 무력화 | 🔴 높음 |
| 4 | 키가 문자열이라 오타를 컴파일 타임에 잡을 수 없다 (`GetData("srvUpdateDeltaMs", 1)`) | 🟠 중간 |
| 5 | 생성자 오버로드 4종 + static 팩토리 4종 — 진입점이 8개 | 🟠 중간 |
| 6 | 스레드 안전성 미문서화. 로딩 후 읽기 전용인지 불명 | 🟠 중간 |

### 개선점 (Phase 14)

- **소스 제너레이터로 강타입 테이블 생성.** `.smt`/CSV 스키마에서 **컴파일 타임에 클래스와 접근자를 생성**하면 문자열 키·런타임 파싱·오타가 전부 사라진다. `MetaDataRuntimeTableMan<T>`가 하려던 것을 제너레이터가 완전히 수행한다 (Phase 7 소스 제너레이터 인프라 재사용)
- **로딩 시점 전수 검증** — 참조 무결성, 범위, 필수 열. 실패는 **시작 실패**로 (Phase 2 옵션 검증과 동일 원칙)
- **서버 테이블을 클라에 그대로 전송**하는 구조는 승계. 단 FlatBuffers 대신 확정된 직렬화 축으로 (Phase 6)
- 테이블 버전을 클라와 대조해 불일치 시 접속 거부 (ROADMAP Phase 14 항목)

### 판정

🟡 **개작** — 3단 구조와 테이블 전송 개념은 승계, 구현은 소스 제너레이터로 대체. → Phase 7·14

---

## `SrvTableM`

`Table/SrvTableM.cs:23`

### 동작

서버 기본 테이블 3종을 로딩하는 진입점.

```csharp
serverConfig    ← SysTable\ServerConfig.smt
clientSettings  ← SysTable\ClientSettings.smt
directScripts   ← SysTable\DirectScripts.smt
   ↓
SrvGlobal.SetSrvGloalVariable(this)     // 전역 변수 세팅
   ↓
_MetaTableLoadingAsync(metaDirPath)     // 앱이 상속해 자기 테이블 로딩
```

`ServerConfigTableRuntimeM` / `ClientSettingTableRuntimeM : LoadableDataInStructM` — `{ optionName, value0, value1 }` 3열 스키마

### 문제점

| # | 문제 | 위치 | 심각도 |
|---|---|---|---|
| 1 | **경로 구분자가 `\` 하드코딩** — `@"SysTable\ServerConfig.smt"`. **Linux에서 파일을 찾지 못한다.** `Path.Combine`을 쓰면서 정작 파일명에 `\`를 박았다 | `:45`, `:53`, `:60` | 🔴 높음 |
| 2 | 파일명이 하드코딩 — 설정 불가 | `:45`,`:53`,`:60` | 🟠 중간 |
| 3 | `directScripts`를 로딩하지만 **런타임 매니저를 만들지 않는다** — 다른 둘과 비대칭 | `:62` | 🟡 낮음 |
| 4 | 테이블 로딩 실패 시 처리가 `MetaDataM` 내부에 위임되어 불명확 | 전체 | 🟠 중간 |
| 5 | 로딩과 **전역 변수 세팅이 결합**되어 있다 — 테스트에서 테이블만 로딩할 수 없다 | `:67` | 🟠 중간 |

### 판정

🟡 **개작** — "기본 테이블 + 앱 확장" 구조는 승계, 경로·설정·전역 결합은 재작성. → Phase 14

**크로스 플랫폼 차단 요인 4번째**로 기록한다 (문서 03의 3개에 추가).

---

## `LoadableDataInStructM` — 🟢 강타입 매핑 기반

`PublicLib/FileM/LoadableDataInStructM.cs` (170줄, 참조 3)

`MetaDataRuntimeTableMan<T>`의 `T` 제약(`where T : LoadableDataInStructM, new()`). 문자열 행을 필드에 채워 넣는 기반 클래스.

파생 예: `ServerConfigTableRuntimeM { string optionName; string value0; string value1; }`

### 판정

🟢 **승계** (개념). 문자열 테이블 → 강타입 객체 매핑은 옳은 방향이다.
단 구현은 **리플렉션 기반일 가능성이 높고**(필드명 매칭), 그렇다면 하드 룰 위반이다. **소스 제너레이터로 대체**한다. → Phase 7·14

---

## `IniFileM` / `IniOptionM`

`PublicLib/FileM/InIFileM.cs:303`, `PublicLib/IniOptionM.cs`

`IniFileM : IEnumerable<KeyValuePair<string, IniSection>>, IDictionary<string, IniSection>` — **완전한 INI 파서**(1073줄). 대소문자 무시 비교자, `IniSection`, `IniValue` 타입 변환(`.ToInt()`, `.ToString()`) 포함.

`IniOptionM` — `OptionServerM.ini` / `OptionClientM.ini`에서 IP·포트를 읽는다. 파생: `IniSrvOptionM`(문서 01), `IniClntOptionM`(문서 05) — **둘 다 실질 내용 없음**.

### 판정

🔴 **폐기**. 잘 만든 파서지만 ChServerM은 `IConfiguration` + Options 패턴으로 간다(Phase 2). INI 형식이 꼭 필요하면 기존 NuGet 패키지를 쓴다 — 1073줄을 자체 유지보수할 이유가 없다.

> 실제로 이 파서로 읽는 값은 **IP와 포트 2개뿐**이다. 1073줄로 2개 값을 읽고 있다.

---

## `FileM` / `StringAnalyzerM`

`PublicLib/FileM/FileM.cs`(349, 참조 3), `FileM/StringAnalyzerM.cs`(193, 참조 1)

`FileM` — 비동기 파일 읽기·쓰기 래퍼. `MetaDataM`, `LogM`, `RoslynCompilerM`이 사용
`StringAnalyzerM` — 문자열 파싱 보조. `MetaDataM`이 사용

### 판정

🔵 **참고**. .NET 표준 API(`File.ReadAllTextAsync` 등)로 충분하다. 별도 래퍼를 두지 않는다.

---

## 🔴 미사용 코드 3,359줄

### `ExcelLibM.cs`(2166) + `ExcelODBCM.cs`(927) — 참조 0

Excel 파일에서 테이블을 읽는 계층. `MetaDataM.GetMetaDataFromExcel(ExcelFileM, sheetName)`이 `ExcelTableM`을 받지만, **그 경로를 호출하는 코드가 없다.** 실제 로딩은 전부 `.smt` 파일 기반(`GetMetaDataFromFileAsync`)이다.

`ExcelODBCM`은 **ODBC로 Excel에 접속**한다 — Windows 전용이고, 드라이버 설치가 필요하며, 서버 런타임에 있을 이유가 없다.

> **추정**: 기획자가 Excel로 관리한 테이블을 `.smt`로 내보내는 **빌드 타임 도구**였을 것이다. 서버 런타임 코드에 남아 있을 이유가 없다.

### 판정

🔴 **폐기**. ChServerM은 **테이블 변환을 빌드 타임 도구로 분리**한다 — 런타임 어셈블리에 Excel 파서를 넣지 않는다. Phase 14의 "CSV/Excel 임포트 검토" 항목은 **빌드 타임 변환**으로 방향을 확정한다.

### `CsvParser.cs`(182) — 참조 0
### `FileWatcherSystemM.cs`(84) — 참조 0

`FileWatcherSystemM`은 ROADMAP Phase 14와 인벤토리에 **"핫 리로드 승계 후보"로 기재**됐으나 실제로는 미참조다. **핫 리로드는 구현이 존재하지 않는다.**

---

## 이 계층의 종합

| 항목 | 판정 | Phase |
|---|---|---|
| **문자열 테이블 → 강타입 매핑 3단 구조** | 🟢 승계 (제너레이터로) | 7·14 |
| **서버 테이블을 클라에 그대로 전송** | 🟢 승계 | 14 |
| **테이블 간 참조를 인덱스로 변환** | 🟢 승계 | 14 |
| **기본 테이블 + 앱 확장 구조** | 🟢 승계 | 14 |
| `MetaDataM` 구현 | 🟡 개작 | 7·14 |
| `SrvTableM` 구현 | 🟡 개작 | 14 |
| INI 파서·설정 | 🔴 폐기 (`IConfiguration`) | 2 |
| 파일 래퍼 | 🔴 폐기 (표준 API) | — |
| **Excel 계층 3,093줄** | 🔴 **폐기 (미사용)** | 14 (빌드 도구로) |
| `CsvParser`, `FileWatcherSystemM` | 🔴 폐기 (미사용) | — |

### 새 코드에 절대 옮기면 안 되는 것

1. `SrvTableM.cs:45,53,60` — **`@"SysTable\ServerConfig.smt"`** 경로 구분자 하드코딩 (Linux 불가)
2. `MetaDataM` — **로딩 시 검증 없음** + `SrvGlobal`의 `Debug.Assert` 검증 (Release 무력화)
3. 문자열 키 기반 테이블 조회 — 오타를 컴파일 타임에 못 잡음
4. 런타임 어셈블리에 **Excel/ODBC 파서 포함**

### 이전 판정 정정

- **`InIFileM.cs`는 사용 중이다.** 파일명(`InIFileM`)과 클래스명(`IniFileM`)이 달라 파일명 검색이 놓쳤다
- **`FileWatcherSystemM`은 미참조다.** ROADMAP Phase 14·`LEGACY-INVENTORY.md`의 "핫 리로드 승계 후보" 기재는 오류였다 — **승계할 구현이 없다**
