using System.Threading.Tasks;

namespace ChServerM.Connections;

/// <summary>
/// 수립된 커넥션 하나를 끝까지 처리한다.
/// </summary>
/// <remarks>
/// <para>
/// 전송과 상위 계층 사이의 유일한 접점이다. 전송은 커넥션을 만들어 넘기고,
/// 반환된 작업이 끝나면 커넥션을 정리한다.
/// </para>
/// <para>
/// <b>반환 시점이 곧 커넥션의 끝이다.</b> "연결됨" 알림이 아니라 <b>전 생애</b>를 맡는 계약이다.
/// 이 모양 덕분에 읽기 루프를 특정 실행 파티션에 고정할 수 있고(ADR-0005),
/// 그러면 프레임마다 큐를 거치는 비용이 사라진다.
/// </para>
/// <para>
/// 여기서 던진 예외는 커넥션 중단으로 처리된다. 프로세스를 죽이지 않는다.
/// </para>
/// </remarks>
public interface IConnectionHandler
{
    /// <summary>커넥션을 처리한다.</summary>
    /// <param name="connection">처리할 커넥션.</param>
    /// <returns>커넥션 처리가 끝나면 완료되는 작업.</returns>
    /// <remarks>
    /// 취소는 <see cref="IConnection.ConnectionClosed"/>로 받는다.
    /// 별도 <c>CancellationToken</c> 인자를 두지 않는 이유는 <b>취소 원천이 둘이 되면
    /// 어느 쪽이 이겼는지 알 수 없기 때문</b>이다.
    /// </remarks>
    Task RunAsync(IConnection connection);
}
