using System;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Security;

namespace ChServerM.Hosting;

/// <summary>
/// 수락 직후 보안 핸드셰이크를 수행하고, 확립된 채널 위에서 내부 핸들러를 돌리는 데코레이터.
/// </summary>
/// <remarks>
/// <para>
/// <b>적용 순서가 이 타입의 존재 이유다.</b> 보안 채널은 수락 직후·프레이밍 시작 전에
/// 확립돼야 한다(ADR-0017) — 순서가 바뀌면 프레이밍이 암호문을 파싱한다.
/// <see cref="ServerBuilder"/> 가 이 데코레이터로 순서를 강제하므로 조립하는 쪽이
/// 순서를 틀릴 방법이 없다.
/// </para>
/// <para>
/// <b>실패 경로.</b> 핸드셰이크 실패는 <see cref="ErrorCode.SecureChannelFailed"/>로
/// 커넥션을 중단하고 카운터 대상으로 기록한다(THREAT-MODEL T-07 — 조용한 실패 금지).
/// 취소는 커넥션이 이미 닫히는 중이라는 뜻이므로 조용히 끝낸다.
/// </para>
/// <para>
/// <b>정리 순서.</b> 내부 핸들러가 끝나면 <c>finally</c> 에서 채널을 정리한다 —
/// 채널 먼저, 원본 커넥션(전송 소유)은 나중. <see cref="ISecureChannel"/> 계약이다.
/// </para>
/// </remarks>
internal sealed class SecuredConnectionHandler : IConnectionHandler
{
    private static readonly EventId SecureChannelFailedEvent = new(6004, "SecureChannelFailed");

    private readonly ITransportSecurity _security;
    private readonly IConnectionHandler _inner;
    private readonly IServerLogger _logger;

    public SecuredConnectionHandler(ITransportSecurity security, IConnectionHandler inner, IServerLogger logger)
    {
        _security = security;
        _inner = inner;
        _logger = logger;
    }

    public async Task RunAsync(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        SecureChannelResult result = await _security
            .SecureAsServerAsync(new ConnectionDuplexPipe(connection), connection.ConnectionClosed)
            .ConfigureAwait(false);

        if (!result.IsEstablished)
        {
            if (result.Status == SecureChannelStatus.Canceled)
            {
                // 커넥션이 이미 닫히는 중이다 — 실패가 아니라 종료 경로다.
                return;
            }

            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.Log(
                    LogLevel.Warning,
                    SecureChannelFailedEvent,
                    connection.Id,
                    null,
                    static (id, _) => $"커넥션 {id} 보안 채널 확립 실패. 커넥션을 닫는다.");
            }

            connection.Abort(ConnectionCloseInfo.ProtocolError(
                result.ToErrorCode(), "보안 채널 핸드셰이크가 실패했다."));
            return;
        }

        ISecureChannel channel = result.Channel!;
        try
        {
#pragma warning disable CA2000 // SecuredConnection 은 추가 자원이 없는 래퍼다 — 채널은 아래 finally 가, 원본 커넥션은 전송이 정리한다(이중 소유 금지).
            await _inner.RunAsync(new SecuredConnection(connection, channel)).ConfigureAwait(false);
#pragma warning restore CA2000
        }
        finally
        {
            // 채널 먼저 정리한다(close_notify + 남은 평문 flush). 원본 커넥션은 전송이 정리한다.
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
