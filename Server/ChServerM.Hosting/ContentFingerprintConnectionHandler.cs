using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Content;
using ChServerM.Diagnostics;
using ChServerM.Handshake;
using ChServerM.Identity;

namespace ChServerM.Hosting;

/// <summary>
/// 버전 협상 직후·프레이밍 직전에 <b>콘텐츠 지문 1왕복</b>을 수행하는 서버 측 데코레이터 (ADR-0044).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 밸런스 표 같은 정적 콘텐츠가 어긋난 채로 접속하면 증상이 <b>한참 뒤에
/// 엉뚱한 모습</b>으로 나타난다 — 클라가 보여 준 값과 서버가 계산한 값이 다르고, 재현하려면
/// 양쪽 데이터를 대조해야 한다. 접속 시점에 지문 하나를 대조하면 그 전부가
/// <b>연결 수립 실패</b>라는 명확한 사건이 된다.
/// </para>
///
/// <para>
/// <b>⚠ Core 도 Hosting 도 이 지문이 무엇의 지문인지 모른다.</b> 데이터 테이블은 선택 축이고
/// Core 는 그 존재를 알지 않는다(CLAUDE.md 3절). 그래서 이 데코레이터가 아는 것은 불투명한
/// 128비트뿐이고, <c>StaticTableSet.Fingerprint</c> 를 <see cref="ContentFingerprint"/> 로
/// 옮기는 것은 <b>앱의 한 줄</b>이다. 그 한 줄이 두 축이 분리돼 있다는 증거다.
/// </para>
///
/// <para>
/// <b>적용 순서.</b> <c>Secured(→ VersionNegotiating(→ 이 타입(→ Framed)))</c> 다.
/// 버전이 먼저인 이유: 프로토콜이 안 맞으면 콘텐츠 비교는 의미가 없고, 거부 사유도
/// 더 근본적인 쪽이 옳다.
/// </para>
///
/// <para>
/// <b>⚠ 왕복은 늘지 않는다.</b> 클라이언트가 <c>ClientHello</c> 와 <c>ContentOffer</c> 를
/// 한 번에 플러시하므로, 협상 데코레이터가 <c>ClientHello</c> 만 소비하고 넘긴 버퍼에
/// 지문 프레임이 <b>이미 들어 있다</b>. 바이트만 늘고 왕복은 그대로다.
/// </para>
///
/// <para>
/// <b>⚠ 양쪽 모두의 스위치다.</b> 서버만 켜면 지문을 기다리다 제한 시간에 걸리고,
/// 클라이언트만 켜면 지문 프레임이 프레이밍 단계로 흘러 형식 오류가 된다. 배포 단위로
/// 함께 켜고 끈다 — 섞어야 하면 프로토콜 버전을 올려 구분한다.
/// </para>
///
/// <para>
/// <b>실패는 전부 시끄럽다.</b> 불일치 = 거부 프레임(사유
/// <see cref="VersionHandshakeCodec.RejectReasonContentMismatch"/>) 송신 후
/// <see cref="ErrorCode.ContentFingerprintMismatch"/> 종료. 형식 위반 =
/// <see cref="ErrorCode.MalformedFrame"/>. 제한 시간 초과 =
/// <see cref="ErrorCode.TransportTimeout"/> — 지문 없이 매달리는 커넥션은 슬롯 점유다(T-16).
/// </para>
///
/// <para>
/// <b>스레드 규약.</b> 교환 동안 이 타입이 <c>Input</c>/<c>Output</c> 의 단독 소유자다.
/// 완료 후 소유권이 내부 핸들러로 넘어간다.
/// </para>
/// </remarks>
internal sealed class ContentFingerprintConnectionHandler : IConnectionHandler
{
    private static readonly EventId MatchedEvent = new(2006, "ContentFingerprintMatched");
    private static readonly EventId MismatchEvent = new(2007, "ContentFingerprintMismatch");

    private readonly ContentFingerprint _expected;
    private readonly ProtocolVersionRange _serverSupported;
    private readonly TimeSpan _handshakeTimeout;
    private readonly IConnectionHandler _inner;
    private readonly TimeProvider _timeProvider;
    private readonly IServerLogger _logger;

    /// <summary>옵션 값을 복사해 만든다 — 동작 중 변경이 판정을 흔들지 않게.</summary>
    public ContentFingerprintConnectionHandler(
        ContentFingerprint expected,
        ProtocolVersionRange serverSupported,
        TimeSpan handshakeTimeout,
        IConnectionHandler inner,
        TimeProvider timeProvider,
        IServerLogger logger)
    {
        _expected = expected;
        _serverSupported = serverSupported;
        _handshakeTimeout = handshakeTimeout;
        _inner = inner;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task RunAsync(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        PipeReader input = connection.Input;

        using CancellationTokenSource timeoutCts = new(_handshakeTimeout, _timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            connection.ConnectionClosed, timeoutCts.Token);

        ContentFingerprint offered;
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
                    LogFailed(connection.Id, "제한 시간 안에 ContentOffer 가 도착하지 않았다");
                    connection.Abort(ConnectionCloseInfo.ProtocolError(
                        ErrorCode.TransportTimeout, "콘텐츠 지문 교환 제한 시간 초과."));
                }

                return;
            }

            if (read.IsCanceled)
            {
                return;
            }

            ReadOnlySequence<byte> buffer = read.Buffer;
            VersionHandshakeStatus status = ContentFingerprintCodec.TryReadOffer(buffer, out offered);

            if (status == VersionHandshakeStatus.Success)
            {
                // 정확히 지문 프레임까지만 소비한다 — 뒤는 프레이밍의 몫이다.
                SequencePosition consumed = buffer.GetPosition(ContentFingerprintCodec.OfferFrameSize);
                input.AdvanceTo(consumed, consumed);
                break;
            }

            if (status == VersionHandshakeStatus.Malformed)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                LogFailed(connection.Id, "협상 다음 프레임이 ContentOffer 동결 형식이 아니다");
                connection.Abort(ConnectionCloseInfo.ProtocolError(
                    ErrorCode.MalformedFrame, "콘텐츠 지문 프레임이 형식에 맞지 않는다."));
                return;
            }

            input.AdvanceTo(buffer.Start, buffer.End);

            if (read.IsCompleted)
            {
                // 지문 없이 끊었다. 프로토콜 위반이라기보다 이탈이다 — 조용히 끝낸다.
                return;
            }
        }

        if (offered != _expected)
        {
            LogMismatch(connection.Id, offered);
            await SendAsync(connection, rejection: true).ConfigureAwait(false);
            connection.Abort(ConnectionCloseInfo.ProtocolError(
                ErrorCode.ContentFingerprintMismatch,
                $"콘텐츠 지문이 다르다. 서버 {_expected}, 클라이언트 {offered}. 클라이언트 콘텐츠 갱신이 필요하다."));
            return;
        }

        if (!await SendAsync(connection, rejection: false).ConfigureAwait(false))
        {
            return;
        }

        LogMatched(connection.Id);

        await _inner.RunAsync(connection).ConfigureAwait(false);
    }

    /// <summary>수락 또는 거부 프레임을 보낸다.</summary>
    /// <returns>계속 진행해도 되면 <see langword="true"/>.</returns>
    /// <remarks>
    /// <b>거부는 최선 노력이다</b> — 실패해도 기다리거나 다시 시도하지 않는다. 거부 경로에서
    /// 상대를 기다리면 그것이 곧 공격 표면이다(협상 데코레이터와 같은 원칙).
    /// </remarks>
    private async ValueTask<bool> SendAsync(IConnection connection, bool rejection)
    {
        try
        {
            PipeWriter output = connection.Output;

            if (rejection)
            {
                Span<byte> frame = output.GetSpan(VersionHandshakeCodec.RejectionFrameSize);
                VersionHandshakeCodec.WriteRejection(
                    frame, _serverSupported, VersionHandshakeCodec.RejectReasonContentMismatch);
                output.Advance(VersionHandshakeCodec.RejectionFrameSize);
            }
            else
            {
                Span<byte> frame = output.GetSpan(ContentFingerprintCodec.AcceptedFrameSize);
                ContentFingerprintCodec.WriteAccepted(frame);
                output.Advance(ContentFingerprintCodec.AcceptedFrameSize);
            }

            FlushResult flush = await output.FlushAsync(connection.ConnectionClosed).ConfigureAwait(false);
            return !rejection && !flush.IsCanceled && !flush.IsCompleted;
        }
        catch (OperationCanceledException)
        {
            // 커넥션이 이미 닫히는 중 — 통지는 포기한다.
            return false;
        }
    }

    private void LogMatched(ConnectionId id)
    {
        // 커넥션당 1회라 핫패스가 아니다. Debug 인 이유: 일치는 정상이고, 운영이 보고 싶은
        // 것은 불일치가 언제 얼마나 나는가다.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.Log(
                LogLevel.Debug,
                MatchedEvent,
                id,
                null,
                static (state, _) => $"커넥션 {state} 콘텐츠 지문 일치.");
        }
    }

    private void LogMismatch(ConnectionId id, ContentFingerprint offered)
    {
        // 롤링 배포 중 "구버전 콘텐츠가 얼마나 남았나" 의 근거다. 카운터 승격은 관측 축에서.
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                MismatchEvent,
                (id, expected: _expected, offered),
                null,
                static (state, _) =>
                    $"커넥션 {state.id} 콘텐츠 지문 불일치: 서버 {state.expected}, 클라이언트 {state.offered}. 거부 후 닫는다.");
        }
    }

    private void LogFailed(ConnectionId id, string reason)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                MismatchEvent,
                (id, reason),
                null,
                static (state, _) => $"커넥션 {state.id} 콘텐츠 지문 교환 실패: {state.reason}. 커넥션을 닫는다.");
        }
    }
}
