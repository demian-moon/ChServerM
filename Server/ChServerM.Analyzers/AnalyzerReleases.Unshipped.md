### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
CHSM3001 | ChServerM.Usage | Warning | async void 메서드·로컬 함수·람다 (UI 이벤트 핸들러 형태 제외)
CHSM3002 | ChServerM.Usage | Warning | async 메서드 안의 블로킹 호출 (.Result / Wait / GetResult / Thread.Sleep)
CHSM3003 | ChServerM.Usage | Warning | MessageContext.Payload 를 필드·속성에 저장 (수명 위반)
