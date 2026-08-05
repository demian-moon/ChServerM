// netstandard2.0 에는 init 접근자·record 가 요구하는 이 타입이 없다.
// 컴파일 타임 전용 마커라 폴리필로 충분하다 — 생성기 어셈블리 밖으로 노출되지 않는다.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit;
