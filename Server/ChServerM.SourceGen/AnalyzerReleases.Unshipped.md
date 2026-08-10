### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
CHSM1001 | ChServerM.Dispatch | Error | 중복 메시지 ID
CHSM1002 | ChServerM.Dispatch | Error | [MessageHandler] 타입이 IMessageHandler\<T\> 미구현
CHSM1003 | ChServerM.Dispatch | Error | 메시지 ID 0 (센티넬)
CHSM1004 | ChServerM.Dispatch | Error | IMessageHandler\<T\> 다중 구현으로 대상 모호
CHSM1005 | ChServerM.Dispatch | Warning | 프레임워크 예약 대역(40001~) 사용
CHSM1006 | ChServerM.Dispatch | Error | 추상/제네릭 정의 핸들러
CHSM1007 | ChServerM.Dispatch | Warning | Hosting 미참조로 등록 코드 미생성
CHSM2001 | ChServerM.DataTable | Error | 행 타입/바깥 타입이 partial 이 아님
CHSM2002 | ChServerM.DataTable | Error | 키 열이 정확히 하나가 아니거나 선택(Optional)임
CHSM2003 | ChServerM.DataTable | Error | 열(partial 속성)이 없음
CHSM2004 | ChServerM.DataTable | Error | 지원하지 않는 열 형식
CHSM2005 | ChServerM.DataTable | Error | 열 이름 중복 또는 생성 멤버와 이름 충돌
CHSM2006 | ChServerM.DataTable | Error | 선택 문자열 열이 널 비허용으로 선언됨
CHSM2007 | ChServerM.DataTable | Error | 범위 제약이 열 종류와 불일치하거나 뒤집힘
CHSM2008 | ChServerM.DataTable | Error | 참조 선언이 잘못됨(대상이 행 타입 아님/열이 string 아님)
CHSM2010 | ChServerM.DataTable | Error | 행 타입이 readonly struct 가 아님
CHSM2011 | ChServerM.DataTable | Error | CSV 헤더에 선언한 열이 없음
CHSM2012 | ChServerM.DataTable | Error | CSV 에 헤더 줄이 없음
CHSM2013 | ChServerM.DataTable | Warning | CSV 헤더에 중복된 열 이름
