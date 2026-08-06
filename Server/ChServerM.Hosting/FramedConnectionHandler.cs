using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Compression;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Execution;
using ChServerM.Framing;
using ChServerM.Time;

namespace ChServerM.Hosting;

/// <summary>
/// 커넥션의 바이트를 프레임으로 잘라 디스패처에 넘기는 읽기 루프.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 전송·프레이밍·디스패치를 잇는 유일한 접합부다. 세 축이 전부
/// 인터페이스로만 들어오므로 <b>이 코드는 TCP인지 인메모리인지, 헤더가 16바이트인지
/// 가변 길이인지, 핸들러가 무엇인지 알지 못한다.</b> 그것이 축 교체가 성립하는 이유다.
/// </para>
/// <para>
/// <b>가장 지키기 어려운 계약 — <c>AdvanceTo</c> 시점.</b>
/// <c>PipeReader.AdvanceTo</c>를 부르는 순간 이전 <c>ReadAsync</c>가 준 버퍼는 무효가 된다.
/// 그런데 핸들러는 그 버퍼를 가리키는 페이로드를 보고 있다. 따라서
/// <b><c>AdvanceTo</c>는 그 읽기에서 나온 모든 프레임의 디스패치가 끝난 뒤에만</b> 부른다.
/// 이 루프가 프레임마다가 아니라 읽기마다 한 번 <c>AdvanceTo</c>하는 이유다.
/// </para>
/// <para>
/// <b>프레임 오류는 재동기화하지 않는다.</b> 어디가 다음 프레임 경계인지 알 수 없기 때문이다.
/// 레거시는 체크섬 예외를 상위에서 삼킨 뒤 상태가 어긋난 채 파싱을 계속했고,
/// 그래서 손상된 프레임 하나가 커넥션 전체를 영구히 오염시켰다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 핸들러 인스턴스 자체는 불변이라 스레드 안전하다.
/// <see cref="RunAsync"/>는 <b>커넥션당 한 번만</b> 호출된다 — 같은 커넥션에 두 번 돌리면
/// 두 루프가 같은 <see cref="PipeReader"/>를 다투게 되어 스트림이 손상된다.
/// </para>
/// <para>
/// <b>실행 모델 통합 (ADR-0008).</b> 실행 모델이 주어지면 프레임 디스패치를 커넥션의
/// 파티션 배타 구간에서 실행한다 — 같은 파티션의 다른 작업과 절대 겹치지 않는다.
/// 프레임 단위로 통합하는 이유: 배타성은 "루프를 어느 스레드에서 시작했는가"로는 얻을 수
/// 없고(<c>await</c> 연속이 이탈한다 — ADR-0008 의 반증), 완료 대기가 가능한 단위로
/// 게시해야 한다. 실행 모델이 없으면 호출 스레드에서 그대로 디스패치한다
/// (무상태 웹 프로필, ADR-0004).
/// </para>
/// <para>
/// <b>할당.</b> 커넥션당 <see cref="MessageContext"/> 하나 + (실행 모델이 있으면)
/// <see cref="PartitionDispatchGate"/> 하나. 프레임당 할당은 0이다
/// (동기적으로 끝나는 핸들러 기준).
/// </para>
/// </remarks>
public sealed class FramedConnectionHandler : IConnectionHandler
{
    private static readonly EventId ProtocolErrorEvent = new(2000, "ProtocolError");
    private static readonly EventId TruncatedFrameEvent = new(2001, "TruncatedFrame");
    private static readonly EventId FragmentViolationEvent = new(2002, "FragmentViolation");
    private static readonly EventId CompressionViolationEvent = new(2003, "CompressionViolation");
    private static readonly EventId DispatchRejectedEvent = new(4003, "DispatchRejected");

    private readonly IFrameDecoder _decoder;
    private readonly IMessageDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly IServerLogger _logger;
    private readonly IExecutionModel? _executionModel;
    private readonly IPayloadCodec? _payloadCodec;
    private readonly bool _closeOnHandlerNotFound;
    private readonly bool _closeOnDeserializationFailure;
    private readonly bool _closeOnPolicyRejection;
    private readonly bool _closeOnHandlerFault;
    private readonly int _maxAssembledMessageLength;
    private readonly int _maxDecompressedMessageLength;

    /// <summary>읽기 루프를 만든다.</summary>
    /// <param name="decoder">프레임 디코더.</param>
    /// <param name="dispatcher">메시지 디스패처.</param>
    /// <param name="options">종료 정책. <see langword="null"/>이면 기본값.</param>
    /// <param name="timeProvider">시간 원본. <see langword="null"/>이면 <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">진단 로거. <see langword="null"/>이면 아무것도 기록하지 않는다.</param>
    /// <param name="executionModel">
    /// 실행 모델. 주어지면 프레임 디스패치가 커넥션의 파티션 배타 구간에서 실행된다
    /// (ADR-0008). <see langword="null"/>이면 호출 스레드에서 그대로 디스패치한다.
    /// </param>
    /// <param name="payloadCodec">
    /// 압축 축. 주어지면 <see cref="FrameFlags.Compressed"/> 프레임을 (조각이면 재조립 후)
    /// 해제해 디스패치한다. <see langword="null"/>인데 압축 프레임이 오면 커넥션을 닫는다 —
    /// 압축된 바이트가 조용히 핸들러에 전달되는 것이 정확히 레거시형 조용한 실패다.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="decoder"/> 또는 <paramref name="dispatcher"/>가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">옵션이 유효하지 않을 때.</exception>
    public FramedConnectionHandler(
        IFrameDecoder decoder,
        IMessageDispatcher dispatcher,
        FramedConnectionOptions? options = null,
        TimeProvider? timeProvider = null,
        IServerLogger? logger = null,
        IExecutionModel? executionModel = null,
        IPayloadCodec? payloadCodec = null)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(dispatcher);

        options ??= new FramedConnectionOptions();
        options.Validate();

        _decoder = decoder;
        _dispatcher = dispatcher;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullServerLogger.Instance;
        _executionModel = executionModel;
        _payloadCodec = payloadCodec;

        // 값을 복사한다. 동작 중에 정책이 바뀌면 같은 커넥션 안에서 판정이 뒤바뀐다.
        _closeOnHandlerNotFound = options.CloseOnHandlerNotFound;
        _closeOnDeserializationFailure = options.CloseOnDeserializationFailure;
        _closeOnPolicyRejection = options.CloseOnPolicyRejection;
        _closeOnHandlerFault = options.CloseOnHandlerFault;
        _maxAssembledMessageLength = options.MaxAssembledMessageLength;
        _maxDecompressedMessageLength = options.MaxDecompressedMessageLength;
    }

    /// <inheritdoc />
    public async Task RunAsync(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        PipeReader input = connection.Input;
        CancellationToken token = connection.ConnectionClosed;

        // 커넥션당 하나. 프레임마다 BeginFrame/EndFrame 으로 재사용한다.
        MessageContext context = new(connection);

        // 실행 모델이 있으면 커넥션을 파티션에 배정하고 게이트를 하나 만든다(ADR-0008).
        // 커넥션당 프레임은 순차이므로 게이트 하나로 충분하다 — 프레임당 할당 0.
        IExecutionPartition? partition = null;
        PartitionDispatchGate? gate = null;
        if (_executionModel is not null)
        {
            partition = _executionModel.GetPartition(connection.Id.ToPartitionKey());
            gate = new PartitionDispatchGate(_dispatcher, context);
        }

        // 조각 재조립 상태. 첫 조각이 올 때 만든다 — 조각을 안 쓰는 커넥션은 비용 0(ADR-0015).
        FragmentAssembler? assembler = null;

        // 압축 해제 버퍼. 코덱이 조립됐을 때만 존재하고, 대여는 압축 프레임에서만 일어난다.
        DecompressionBuffer? decompression = _payloadCodec is null ? null : new DecompressionBuffer();

        try
        {
            while (true)
            {
                ReadResult read;
                try
                {
                    read = await input.ReadAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 커넥션이 닫혔다. 정상 경로다.
                    return;
                }

                if (read.IsCanceled)
                {
                    return;
                }

                ReadOnlySequence<byte> buffer = read.Buffer;

                // AdvanceTo 는 아래에서 정확히 한 번 부른다. 그 전까지 페이로드가 유효하다.
                SequencePosition consumed = buffer.Start;
                SequencePosition examined = buffer.Start;
                ConnectionCloseInfo? closeInfo = null;

                while (buffer.Length > 0)
                {
                    FrameDecodeResult decoded = _decoder.Decode(buffer);

                    if (decoded.Status == FrameDecodeStatus.NeedMoreData)
                    {
                        // examined 를 버퍼 끝으로 둬야 파이프가 더 읽는다. 여기를 틀리면 교착.
                        examined = decoded.Examined;
                        break;
                    }

                    if (decoded.IsFatal)
                    {
                        examined = decoded.Examined;
                        closeInfo = OnProtocolError(decoded);
                        break;
                    }

                    DispatchStatus status = DispatchStatus.Handled;
                    FrameFlags frameFlags = decoded.Envelope.Flags;

                    if ((frameFlags & (FrameFlags.Fragmented | FrameFlags.EndOfMessage)) != 0
                        || assembler is { InProgress: true })
                    {
                        // 조각 경로 — 계약 위반이면 closeInfo, 마지막 조각이면 재조립분 디스패치.
                        bool dispatchAssembled;
                        (closeInfo, dispatchAssembled) = AccumulateFragment(ref assembler, decoded);

                        if (closeInfo is null && dispatchAssembled)
                        {
                            try
                            {
                                (closeInfo, status) = await DispatchWithDecompressionAsync(
                                    context, gate, partition,
                                    assembler!.AssembledEnvelope, assembler.AssembledPayload,
                                    decompression, token).ConfigureAwait(false);
                            }
                            finally
                            {
                                // 완성 즉시 반납 — 유휴 커넥션이 재조립 버퍼를 붙들지 않는다.
                                assembler!.Reset();
                            }
                        }
                    }
                    else
                    {
                        (closeInfo, status) = await DispatchWithDecompressionAsync(
                            context, gate, partition, decoded.Envelope, decoded.Payload,
                            decompression, token).ConfigureAwait(false);
                    }

                    buffer = buffer.Slice(decoded.Consumed);
                    consumed = decoded.Consumed;
                    examined = decoded.Consumed;

                    if (closeInfo is not null)
                    {
                        // 조각·압축 계약 위반이 확정됐다 — 같은 읽기의 나머지 프레임을
                        // 처리하지 않는다. 위반 이후의 프레임을 계속 디스패치하면
                        // "종료가 결정된 커넥션에서 일이 진행되는" 창이 생긴다.
                        break;
                    }

                    if (status != DispatchStatus.Handled)
                    {
                        closeInfo = OnDispatchNotHandled(context, status);
                        if (closeInfo is not null)
                        {
                            break;
                        }
                    }
                }

                input.AdvanceTo(consumed, examined);

                if (closeInfo is { } info)
                {
                    connection.Abort(info);
                    return;
                }

                if (read.IsCompleted)
                {
                    if (buffer.Length > 0)
                    {
                        // 상대가 프레임 중간에 끊었다. 남은 바이트를 무시하고 넘어가면
                        // "왜 마지막 요청이 처리되지 않았는가"를 아무도 모르게 된다.
                        LogTruncatedFrame(buffer.Length);
                        connection.Abort(new ConnectionCloseInfo(
                            CloseReason.ProtocolError,
                            ErrorCode.MalformedFrame,
                            "프레임이 완성되기 전에 상대가 스트림을 닫았다."));
                    }
                    else if (assembler is { InProgress: true })
                    {
                        // 조각 메시지가 완성되기 전에 스트림이 끝났다 — 잘린 프레임과 같은 부류다.
                        LogFragmentViolation("마지막 조각(EndOfMessage)이 오기 전에 상대가 스트림을 닫았다.");
                        connection.Abort(new ConnectionCloseInfo(
                            CloseReason.ProtocolError,
                            ErrorCode.MalformedFrame,
                            "조각 메시지가 완성되기 전에 상대가 스트림을 닫았다."));
                    }

                    return;
                }
            }
        }
        finally
        {
            // 재조립·해제 버퍼는 풀 대여물이다 — 어떤 종료 경로에서도 반납한다.
            assembler?.Reset();
            decompression?.Reset();

            // 읽기 쪽을 반드시 닫는다. 남겨두면 쓰기 측이 백프레셔로 영원히 대기한다.
            await input.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>조각 프레임 하나를 계약 검사와 함께 누적한다.</summary>
    /// <returns>
    /// 계약 위반이면 종료 정보(첫 항목), 마지막 조각이라 재조립분을 디스패치해야 하면
    /// 둘째 항목이 <see langword="true"/>.
    /// </returns>
    /// <remarks>
    /// 계약(ADR-0015): 조각은 연속이어야 하고(사이에 다른 프레임 금지), 같은
    /// <c>MessageId</c> 여야 하며, <see cref="FrameFlags.EndOfMessage"/> 는
    /// <see cref="FrameFlags.Fragmented"/> 와 함께여야 한다. 누적 길이에는 상한이 있다.
    /// 위반은 전부 커넥션 종료다 — 조각 상태가 어긋난 채 계속하면 재조립 결과가
    /// 조용히 오염된다(레거시 desync 와 같은 부류).
    /// </remarks>
    private (ConnectionCloseInfo? CloseInfo, bool DispatchAssembled) AccumulateFragment(
        ref FragmentAssembler? assembler, in FrameDecodeResult decoded)
    {
        FrameFlags flags = decoded.Envelope.Flags;

        if ((flags & FrameFlags.Fragmented) == 0)
        {
            // EndOfMessage 단독이거나, 재조립 중에 비조각 프레임이 끼었다.
            string reason = (flags & FrameFlags.EndOfMessage) != 0
                ? "EndOfMessage 는 Fragmented 와 함께여야 한다."
                : "조각 메시지가 진행 중일 때는 조각 프레임만 올 수 있다.";
            LogFragmentViolation(reason);
            return (ConnectionCloseInfo.ProtocolError(ErrorCode.InvalidFrameFlags, reason), false);
        }

        if (_maxAssembledMessageLength == 0)
        {
            const string Reason = "이 서버는 조각 재조립을 받지 않는다(MaxAssembledMessageLength=0).";
            LogFragmentViolation(Reason);
            return (ConnectionCloseInfo.ProtocolError(ErrorCode.InvalidFrameFlags, Reason), false);
        }

        assembler ??= new FragmentAssembler(_maxAssembledMessageLength);

        if (!assembler.TryAppend(decoded.Envelope, decoded.Payload, out FragmentError error))
        {
            if (error == FragmentError.TooLarge)
            {
                LogFragmentViolation("재조립 상한 초과.");
                return (new ConnectionCloseInfo(
                    CloseReason.ResourceLimit,
                    ErrorCode.FrameTooLarge,
                    $"조각 재조립 길이가 상한({_maxAssembledMessageLength}B)을 넘었다."), false);
            }

            LogFragmentViolation("조각 사이에 다른 메시지 식별자가 끼었다.");
            return (ConnectionCloseInfo.ProtocolError(
                ErrorCode.InvalidFrameFlags, "조각 사이에 다른 메시지 식별자가 끼었다."), false);
        }

        return (null, (flags & FrameFlags.EndOfMessage) != 0);
    }

    private void LogFragmentViolation(string reason)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                FragmentViolationEvent,
                reason,
                null,
                static (state, _) => $"조각 재조립 계약 위반: {state}");
        }
    }

    /// <summary>압축 프레임이면 해제한 뒤, 평문이면 그대로 디스패치한다.</summary>
    /// <returns>첫 항목이 <see langword="null"/>이 아니면 압축 계약 위반 — 커넥션을 닫는다.</returns>
    /// <remarks>
    /// <para>
    /// 압축 축이 조립되지 않았는데 <see cref="FrameFlags.Compressed"/> 프레임이 오면 종료다 —
    /// 압축된 바이트가 플래그만 단 채 조용히 핸들러에 전달되는 것이 정확히 레거시형
    /// 조용한 실패다(T-07). 해제 실패(손상·해제 상한 초과·알고리즘 불일치)도 종료다(T-18) —
    /// 상한 검사는 코덱 계약이 <b>버퍼를 잡기 전에</b> 수행한다.
    /// </para>
    /// <para>
    /// 해제에 성공하면 <see cref="FrameFlags.Compressed"/> 를 지우고 디스패치한다 —
    /// 핸들러가 보는 페이로드는 평문이므로 플래그를 남기면 거짓말이 된다.
    /// 해제 버퍼는 디스패치 직후 <c>finally</c> 로 반납한다(페이로드 참조는
    /// <c>EndFrame</c> 이 이미 끊었다).
    /// </para>
    /// </remarks>
    private async ValueTask<(ConnectionCloseInfo? CloseInfo, DispatchStatus Status)> DispatchWithDecompressionAsync(
        MessageContext context,
        PartitionDispatchGate? gate,
        IExecutionPartition? partition,
        MessageEnvelope envelope,
        ReadOnlySequence<byte> payload,
        DecompressionBuffer? decompression,
        CancellationToken token)
    {
        if ((envelope.Flags & FrameFlags.Compressed) == 0)
        {
            DispatchStatus plainStatus = gate is null
                ? await DispatchFrameAsync(context, envelope, payload, token).ConfigureAwait(false)
                : await gate.DispatchExclusiveAsync(
                    partition!, envelope, payload,
                    MonotonicTimestamp.Now(_timeProvider), token).ConfigureAwait(false);
            return (null, plainStatus);
        }

        if (_payloadCodec is null || decompression is null)
        {
            LogCompressionViolation("압축 프레임을 받았지만 압축 축이 조립되지 않았다.");
            return (ConnectionCloseInfo.ProtocolError(
                ErrorCode.InvalidFrameFlags, "이 조립은 압축 프레임을 받지 않는다."), default);
        }

        try
        {
            if (!_payloadCodec.TryDecode(payload, decompression, _maxDecompressedMessageLength, out _))
            {
                LogCompressionViolation(
                    $"압축 해제 실패 — 손상이거나 해제 상한({_maxDecompressedMessageLength}B) 초과이거나 알고리즘 불일치다.");
                return (ConnectionCloseInfo.ProtocolError(
                    ErrorCode.MalformedFrame, "압축 페이로드를 해제할 수 없다."), default);
            }

            MessageEnvelope plain = new(
                envelope.MessageId, envelope.Flags & ~FrameFlags.Compressed, envelope.Sequence);

            DispatchStatus status = gate is null
                ? await DispatchFrameAsync(context, plain, decompression.WrittenSequence, token).ConfigureAwait(false)
                : await gate.DispatchExclusiveAsync(
                    partition!, plain, decompression.WrittenSequence,
                    MonotonicTimestamp.Now(_timeProvider), token).ConfigureAwait(false);
            return (null, status);
        }
        finally
        {
            // 사용 직후 반납 — 유휴 커넥션이 해제 버퍼를 붙들지 않는다(재조립 버퍼와 같은 규약).
            decompression.Reset();
        }
    }

    private void LogCompressionViolation(string reason)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                CompressionViolationEvent,
                reason,
                null,
                static (state, _) => $"압축 계약 위반: {state} 커넥션을 닫는다.");
        }
    }

    /// <summary>
    /// 압축 해제 출력용 커넥션 소유 풀 대여 버퍼.
    /// </summary>
    /// <remarks>
    /// <see cref="FragmentAssembler"/> 와 같은 수명 규약이다 — 대여는 압축 프레임에서만
    /// 일어나고, 사용 직후 <see cref="Reset"/> 으로 반납한다. 커넥션 종료 경로의
    /// <c>finally</c> 가 마지막 안전망이다. 커넥션당 하나이므로 동기화가 필요 없다
    /// (읽기 루프 단일 소유).
    /// </remarks>
    private sealed class DecompressionBuffer : IBufferWriter<byte>
    {
        private byte[]? _rented;
        private int _written;

        /// <summary>지금까지 쓰인 바이트의 시퀀스 뷰. <see cref="Reset"/> 이후 무효.</summary>
        public ReadOnlySequence<byte> WrittenSequence =>
            _rented is null ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(_rented, 0, _written);

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _rented!.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _rented!.AsSpan(_written);
        }

        /// <summary>대여물을 반납하고 초기 상태로 돌아간다.</summary>
        public void Reset()
        {
            if (_rented is not null)
            {
                ArrayPool<byte>.Shared.Return(_rented);
                _rented = null;
            }

            _written = 0;
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint < 1)
            {
                sizeHint = 1;
            }

            if (_rented is not null && _rented.Length - _written >= sizeHint)
            {
                return;
            }

            byte[] bigger = ArrayPool<byte>.Shared.Rent(_written + sizeHint);
            if (_rented is not null)
            {
                _rented.AsSpan(0, _written).CopyTo(bigger);
                ArrayPool<byte>.Shared.Return(_rented);
            }

            _rented = bigger;
        }
    }

    /// <summary>프레임 하나를 디스패치한다. 페이로드 참조 해제를 <c>finally</c>로 보장한다.</summary>
    private async ValueTask<DispatchStatus> DispatchFrameAsync(
        MessageContext context,
        MessageEnvelope envelope,
        ReadOnlySequence<byte> payload,
        CancellationToken token)
    {
        context.BeginFrame(envelope, payload, MonotonicTimestamp.Now(_timeProvider), token);

        try
        {
            return await _dispatcher.DispatchAsync(context).ConfigureAwait(false);
        }
        finally
        {
            // 예외가 나도 반드시 참조를 끊는다. 남은 참조는 곧 반납될 버퍼를 가리킨다.
            context.EndFrame();
        }
    }

    private ConnectionCloseInfo OnProtocolError(in FrameDecodeResult decoded)
    {
        ErrorCode errorCode = decoded.ToErrorCode();

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                ProtocolErrorEvent,
                decoded.Status,
                null,
                static (status, _) => $"프레임 디코딩 실패({status}). 재동기화가 불가능하므로 커넥션을 닫는다.");
        }

        return ConnectionCloseInfo.ProtocolError(errorCode, decoded.Status.ToString());
    }

    /// <summary>처리되지 않은 디스패치 결과를 기록하고, 닫아야 하면 종료 정보를 만든다.</summary>
    /// <returns>커넥션을 닫아야 하면 종료 정보, 계속해도 되면 <see langword="null"/>.</returns>
    private ConnectionCloseInfo? OnDispatchNotHandled(MessageContext context, DispatchStatus status)
    {
        // 닫든 말든 반드시 기록한다. 조용한 유실을 만들지 않는 것이 이 분기의 목적이다.
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                DispatchRejectedEvent,
                (MessageId: context.Envelope.MessageId.Value, Status: status),
                null,
                static (state, _) => $"메시지 {state.MessageId} 가 처리되지 않았다: {state.Status}");
        }

        return status switch
        {
            DispatchStatus.HandlerNotFound when _closeOnHandlerNotFound =>
                new ConnectionCloseInfo(CloseReason.ProtocolError, ErrorCode.HandlerNotFound),

            DispatchStatus.DeserializationFailed when _closeOnDeserializationFailure =>
                new ConnectionCloseInfo(CloseReason.ProtocolError, ErrorCode.DeserializationFailed),

            DispatchStatus.RejectedByPolicy when _closeOnPolicyRejection =>
                new ConnectionCloseInfo(CloseReason.ApplicationError, ErrorCode.AuthorizationFailed),

            DispatchStatus.RejectedByState =>
                new ConnectionCloseInfo(CloseReason.ProtocolError, ErrorCode.MessageNotAllowedInState),

            // 옵션 게이트가 없다 — 인증 실패 = 즉시 종료는 정책이 아니라 불변이다(T-20).
            DispatchStatus.RejectedByAuthentication =>
                new ConnectionCloseInfo(CloseReason.ApplicationError, ErrorCode.AuthenticationFailed),

            DispatchStatus.Faulted when _closeOnHandlerFault =>
                new ConnectionCloseInfo(CloseReason.ApplicationError, ErrorCode.HandlerFaulted),

            DispatchStatus.Canceled =>
                new ConnectionCloseInfo(CloseReason.ShuttingDown, ErrorCode.OperationCanceled),

            DispatchStatus.RejectedByBackpressure =>
                new ConnectionCloseInfo(CloseReason.ResourceLimit, ErrorCode.QueueFull),

            // 속도 제한은 커넥션을 닫지 않는다(불변) — 일시적 제한이라 종료하면 재접속
            // 폭풍이 된다. 프레임만 버리고 로그·메트릭으로 관측된다(위 로그 + 상위
            // MetricsMiddleware 의 DispatchFailures). null 은 "계속"을 뜻한다.
            DispatchStatus.RejectedByRateLimit => null,

            _ => null,
        };
    }

    private void LogTruncatedFrame(long remaining)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                TruncatedFrameEvent,
                remaining,
                null,
                static (bytes, _) => $"프레임 중간에 스트림이 끝났다. 버려진 바이트: {bytes}");
        }
    }
}
