using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Features;
using ChServerM.Handshake;
using ChServerM.Identity;

namespace ChServerM.Hosting;

/// <summary>
/// 프레이밍 시작 전에 버전 협상 1왕복을 수행하는 서버 측 데코레이터 (ADR-0017 결정 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>적용 순서가 이 타입의 존재 이유다.</b> 협상은 보안 채널 <b>안</b>·프레이밍 <b>전</b>에
/// 일어난다 — <see cref="ServerBuilder"/> 가 <c>SecuredConnectionHandler(→ 이 타입(→
/// FramedConnectionHandler))</c> 순서로 감싸므로 조립하는 쪽이 순서를 틀릴 수 없다.
/// 협상이 TLS 안이라 다운그레이드 방지(R-4)가 별도 장치 없이 충족된다.
/// </para>
/// <para>
/// <b>와이어는 프레이밍 축을 타지 않는다.</b> <see cref="VersionHandshakeCodec"/> 의 동결
/// 레이아웃으로 파이프에서 직접 읽고 쓴다(R-2). 협상이 끝나면 정확히 소비한 바이트까지만
/// <c>AdvanceTo</c> 하므로, 클라이언트가 핸드셰이크 직후 파이프라인으로 보낸 프레임은
/// 그대로 내부 핸들러(프레이밍)의 첫 읽기에 넘어간다 — 바이트 유실 없음.
/// </para>
/// <para>
/// <b>실패는 전부 시끄럽다.</b> 교집합 없음 = 거부 프레임(서버 지원 구간 포함, R-3) 송신 후
/// <b>정상 종료</b> — Abort 가 아니다. Abort 는 대기 중인 송신 데이터를 보장하지 않아
/// 거부 프레임 자체를 파괴할 수 있다(느린 러너에서 실제 유실 3회). 형식 위반 =
/// <see cref="ErrorCode.MalformedFrame"/> 종료. 제한 시간 초과 =
/// <see cref="ErrorCode.TransportTimeout"/> 종료(T-16 — 협상 없이 매달리는 커넥션은
/// 슬롯 점유 공격이다). 뒤의 둘은 실을 데이터가 없으므로 Abort 가 맞다. 결과는 버전별
/// 로그로 남는다(R-5 — 롤링 배포 중 "구버전이 얼마나 남았나"의 근거).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 협상 동안 이 타입이 <c>Input</c>/<c>Output</c> 의 단독 소유자다.
/// 완료 후 소유권이 내부 핸들러로 넘어간다 — <see cref="IConnection"/> 의 단독 소유 규약이
/// 시간 축에서 순차로 이어지는 것이다.
/// </para>
/// </remarks>
internal sealed class VersionNegotiatingConnectionHandler : IConnectionHandler
{
    private static readonly EventId NegotiatedEvent = new(2004, "VersionNegotiated");
    private static readonly EventId NegotiationFailedEvent = new(2005, "VersionNegotiationFailed");

    private readonly ProtocolVersionRange _supportedVersions;
    private readonly TimeSpan _handshakeTimeout;
    private readonly IConnectionHandler _inner;
    private readonly TimeProvider _timeProvider;
    private readonly IServerLogger _logger;

    /// <summary>옵션 값을 복사해 만든다 — 동작 중 옵션 변경이 판정을 흔들지 않게.</summary>
    public VersionNegotiatingConnectionHandler(
        VersionNegotiationOptions options,
        IConnectionHandler inner,
        TimeProvider timeProvider,
        IServerLogger logger)
    {
        _supportedVersions = options.SupportedVersions;
        _handshakeTimeout = options.HandshakeTimeout;
        _inner = inner;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task RunAsync(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        PipeReader input = connection.Input;

        // 제한 시간은 CTS 가 센다 — 협상 프레임 없이 매달리는 커넥션을 끊는다(T-16).
        using CancellationTokenSource timeoutCts = new(_handshakeTimeout, _timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            connection.ConnectionClosed, timeoutCts.Token);

        ProtocolVersionRange clientSupported;
        while (true)
        {
            ReadResult read;
            try
            {
                read = await input.ReadAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (timeoutCts.IsCancellationRequested && !connection.ConnectionClosed.IsCancellationRequested)
                {
                    LogFailed(connection.Id, "제한 시간 안에 ClientHello 가 도착하지 않았다");
                    connection.Abort(ConnectionCloseInfo.ProtocolError(
                        ErrorCode.TransportTimeout, "버전 협상 제한 시간 초과."));
                }

                // 커넥션이 닫히는 중이면 실패가 아니라 종료 경로다.
                return;
            }

            if (read.IsCanceled)
            {
                return;
            }

            ReadOnlySequence<byte> buffer = read.Buffer;
            VersionHandshakeStatus status = VersionHandshakeCodec.TryReadClientHello(buffer, out clientSupported);

            if (status == VersionHandshakeStatus.Success)
            {
                // 정확히 핸드셰이크 바이트까지만 소비한다 — 뒤는 프레이밍의 몫이다.
                SequencePosition consumed = buffer.GetPosition(VersionHandshakeCodec.ClientHelloFrameSize);
                input.AdvanceTo(consumed, consumed);
                break;
            }

            if (status == VersionHandshakeStatus.Malformed)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                LogFailed(connection.Id, "첫 프레임이 ClientHello 동결 형식이 아니다");
                connection.Abort(ConnectionCloseInfo.ProtocolError(
                    ErrorCode.MalformedFrame, "버전 협상 프레임이 형식에 맞지 않는다."));
                return;
            }

            // NeedMoreData — examined 를 끝으로 둬야 파이프가 더 읽는다.
            input.AdvanceTo(buffer.Start, buffer.End);

            if (read.IsCompleted)
            {
                // 인사 없이 끊었다. 프로토콜 위반이라기보다 이탈이다 — 조용히 끝낸다.
                return;
            }
        }

        if (!ProtocolVersionRange.TrySelect(_supportedVersions, clientSupported, out ushort selected))
        {
            LogRejected(connection.Id, clientSupported);
            await SendRejectionAsync(connection).ConfigureAwait(false);

            // ⚠ 여기서 Abort 를 부르면 안 된다. Abort 는 대기 중인 송신 데이터를 보장하지
            // 않으므로(IConnection 계약), 방금 실은 거부 프레임이 소켓에 도달하기 전에
            // 파괴될 수 있다 — 느린 CI 러너에서 실제로 유실됐다(2026-08-10~11, 3회).
            // 그냥 반환하면 소유자(전송 수락 루프)의 정상 종료가 남은 데이터를 내보내고
            // FIN 을 보낸다. 종료는 전송의 ShutdownTimeout 으로 유계라, 거부 경로가
            // 상대를 무한정 기다리는 공격 표면이 되지도 않는다. 거부 사유의 관측 정본은
            // 위의 R-5 로그다.
            return;
        }

        try
        {
            PipeWriter output = connection.Output;
            Span<byte> frame = output.GetSpan(VersionHandshakeCodec.ServerHelloFrameSize);
            VersionHandshakeCodec.WriteServerHello(frame, selected);
            output.Advance(VersionHandshakeCodec.ServerHelloFrameSize);

            FlushResult flush = await output.FlushAsync(linked.Token).ConfigureAwait(false);
            if (flush.IsCanceled || flush.IsCompleted)
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        connection.Features.Set<IProtocolVersionFeature>(new NegotiatedVersionFeature(selected));
        LogNegotiated(connection.Id, selected);

        await _inner.RunAsync(connection).ConfigureAwait(false);
    }

    /// <summary>거부 통지를 최선 노력으로 보낸다 — 실패해도 기다리거나 다시 시도하지 않는다.</summary>
    /// <remarks>
    /// 거부 경로에서 상대를 기다리면 그것이 곧 공격 표면이다(전송의 RejectionNotice 와
    /// 같은 원칙). 22바이트라 로컬 버퍼에는 항상 들어가고, flush 실패는 무시한다.
    /// </remarks>
    private async ValueTask SendRejectionAsync(IConnection connection)
    {
        try
        {
            PipeWriter output = connection.Output;
            Span<byte> frame = output.GetSpan(VersionHandshakeCodec.RejectionFrameSize);
            VersionHandshakeCodec.WriteRejection(frame, _supportedVersions);
            output.Advance(VersionHandshakeCodec.RejectionFrameSize);
            _ = await output.FlushAsync(connection.ConnectionClosed).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 커넥션이 이미 닫히는 중 — 통지는 포기한다.
        }
    }

    private void LogNegotiated(ConnectionId id, ushort selected)
    {
        // R-5: 버전 분포 관측의 근거. 커넥션당 1회라 Information 이어도 핫패스가 아니다.
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.Log(
                LogLevel.Information,
                NegotiatedEvent,
                (id, selected),
                null,
                static (state, _) => $"커넥션 {state.id} 버전 협상 완료: v{state.selected}");
        }
    }

    private void LogRejected(ConnectionId id, ProtocolVersionRange clientSupported)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                NegotiationFailedEvent,
                (id, server: _supportedVersions, client: clientSupported),
                null,
                static (state, _) =>
                    $"커넥션 {state.id} 버전 교집합 없음: 서버 {state.server}, 클라이언트 {state.client}. 거부 후 닫는다.");
        }
    }

    private void LogFailed(ConnectionId id, string reason)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                NegotiationFailedEvent,
                (id, reason),
                null,
                static (state, _) => $"커넥션 {state.id} 버전 협상 실패: {state.reason}. 커넥션을 닫는다.");
        }
    }
}

/// <summary>협상 결과를 커넥션 피처로 나르는 불변 구현.</summary>
internal sealed class NegotiatedVersionFeature : IProtocolVersionFeature
{
    public NegotiatedVersionFeature(ushort negotiatedVersion) => NegotiatedVersion = negotiatedVersion;

    public ushort NegotiatedVersion { get; }
}
