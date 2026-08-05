using System;
using System.IO.Pipelines;

namespace ChServerM.Security;

/// <summary>
/// 확립된 보안 채널 — 평문 측 양방향 파이프.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IDuplexPipe.Input"/>/<see cref="IDuplexPipe.Output"/>은 <b>평문 측</b>이다.
/// 프레이밍·디스패치는 이 파이프만 본다. 암호문은 채널 내부에서
/// <see cref="ITransportSecurity"/>에 넘겼던 원본 파이프로 흐른다.
/// </para>
/// <para>
/// <b>수명·소유권.</b> <see cref="IAsyncDisposable.DisposeAsync"/>는 보안 계층의
/// 자원(핸드셰이크 상태·내부 펌프·대여 버퍼)을 정리하고 원본 파이프에 완결을
/// 전파한다. <b>원본 전송(소켓)의 수명은 계속 커넥션이 단일 소유한다</b> —
/// 소유자가 둘이면 어느 쪽이 닫았는지 알 수 없고, 취소 단일 원천 규약
/// (<c>IConnection.ConnectionClosed</c>, Phase 1)과 충돌한다. 정리 순서는
/// 호스팅이 강제한다: 채널 먼저, 커넥션 나중.
/// </para>
/// <para>
/// <b>스레드 규약.</b> <c>IConnection</c>과 동일 — <see cref="IDuplexPipe.Input"/>은
/// 읽기 루프 하나가, <see cref="IDuplexPipe.Output"/>은 쓰기 경로 하나가 소유한다.
/// </para>
/// </remarks>
public interface ISecureChannel : IDuplexPipe, IAsyncDisposable
{
}
