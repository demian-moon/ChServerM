using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;

namespace ChServerM.Transports;

/// <summary>
/// 나가는 커넥션을 만드는 전송.
/// </summary>
/// <remarks>
/// <para>
/// 서버와 클라이언트가 <b>같은 <see cref="IConnection"/></b>을 쓴다. 그래서
/// 프레이밍·직렬화·디스패치 계층을 양쪽이 공유하고, 서버 핸들러를 클라이언트에서
/// 그대로 돌리는 것도 가능하다 — 서버-투-서버 통신이 특별한 경로가 되지 않는다.
/// </para>
/// <para>
/// <b>재접속은 이 계층의 책임이 아니다.</b> 백오프·재시도 정책은 상위에서 조립한다.
/// 여기서 재접속을 감추면 "연결이 살아 있다"는 거짓 신호를 주게 되고,
/// 상위 계층이 세션 재수립(인증·상태 복원)을 건너뛴다.
/// </para>
/// </remarks>
public interface IClientTransport : IAsyncDisposable
{
    /// <summary>원격 종단에 연결한다.</summary>
    /// <param name="endPoint">연결할 주소.</param>
    /// <param name="cancellationToken">연결 시도의 취소 토큰.</param>
    /// <returns>수립된 커넥션.</returns>
    /// <remarks>
    /// 연결 실패는 <b>예외</b>다. 재시도할지 포기할지는 호출자의 정책이며,
    /// 실패를 <see langword="null"/>로 돌려주면 그 정책이 조용히 사라진다.
    /// </remarks>
    ValueTask<IConnection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default);
}
