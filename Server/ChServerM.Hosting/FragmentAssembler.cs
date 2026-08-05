using System;
using System.Buffers;
using ChServerM.Framing;

namespace ChServerM.Hosting;

/// <summary>
/// 조각난 논리 메시지(<see cref="FrameFlags.Fragmented"/>)를 하나로 재조립하는 커넥션당 상태.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 프레임 페이로드에는 상한(<c>MaxPayloadLength</c>)이 있다. 그보다 큰
/// 논리 메시지는 조각으로 나뉘어 오고, 어딘가는 그것을 다시 붙여야 한다. 디코더는
/// 무상태·공유 인스턴스라 이 상태를 가질 수 없으므로(Phase 4 결정 — 상태 머신 없는
/// 디코더), 커넥션 소유의 읽기 루프가 이 타입을 통해 재조립한다.
/// </para>
/// <para>
/// <b>상한이 계약이다.</b> 누적 길이가 <c>maxAssembledLength</c> 를 넘으면 즉시 실패한다 —
/// 마지막 조각이 오지 않는 부분 메시지를 무한정 들고 있으면 그 자체가 메모리 고갈
/// 공격 경로다(ADR-0007 미해결 항목의 해소, ADR-0015). 조각은 <b>도착 즉시 복사</b>된다 —
/// 파이프 버퍼는 <c>AdvanceTo</c> 로 반납되므로 참조를 들고 있을 수 없다.
/// </para>
/// <para>
/// <b>수명·소유권 규약.</b> 버퍼는 <see cref="ArrayPool{T}.Shared"/> 대여물이고
/// <b>이 타입이 반납 책임자다.</b> 메시지 완성 후와 커넥션 종료 시(읽기 루프의
/// <c>finally</c>) 반드시 <see cref="Reset"/> 을 불러야 한다 — 완성 즉시 반납하므로
/// 유휴 커넥션은 재조립 메모리를 붙들지 않는다(레거시 ArrayPool 미반납 재발 방지).
/// </para>
/// <para><b>스레드 규약.</b> 읽기 루프 전용이다. 스레드 안전하지 않다.</para>
/// </remarks>
internal sealed class FragmentAssembler
{
    private readonly int _maxAssembledLength;
    private byte[]? _buffer;
    private int _length;
    private MessageEnvelope _firstEnvelope;

    public FragmentAssembler(int maxAssembledLength) => _maxAssembledLength = maxAssembledLength;

    /// <summary>조각을 모으는 중인가. 참이면 다음 프레임도 같은 메시지의 조각이어야 한다.</summary>
    public bool InProgress { get; private set; }

    /// <summary>조각 하나를 누적한다.</summary>
    /// <param name="envelope">조각 프레임의 엔벨로프.</param>
    /// <param name="payload">조각 페이로드. 호출이 끝나면 무효다 — 안에서 복사한다.</param>
    /// <param name="error">실패 시 원인.</param>
    /// <returns>누적했으면 <see langword="true"/>. 실패는 프로토콜 위반이며 커넥션을 닫아야 한다.</returns>
    public bool TryAppend(in MessageEnvelope envelope, in ReadOnlySequence<byte> payload, out FragmentError error)
    {
        if (!InProgress)
        {
            InProgress = true;
            _firstEnvelope = envelope;
        }
        else if (envelope.MessageId != _firstEnvelope.MessageId)
        {
            // 조각 사이에 다른 메시지의 조각이 끼었다 — 어느 메시지가 정본인지 알 수 없다.
            error = FragmentError.MessageIdMismatch;
            return false;
        }

        long assembled = _length + payload.Length;
        if (assembled > _maxAssembledLength)
        {
            error = FragmentError.TooLarge;
            return false;
        }

        EnsureCapacity((int)assembled);
        payload.CopyTo(_buffer.AsSpan(_length));
        _length = (int)assembled;

        error = FragmentError.None;
        return true;
    }

    /// <summary>재조립이 끝난 메시지의 엔벨로프. 첫 조각의 것에서 조각 플래그만 걷어낸다.</summary>
    /// <remarks>
    /// <see cref="FrameFlags.Compressed"/>/<see cref="FrameFlags.Encrypted"/> 는 남는다 —
    /// 변환은 논리 메시지 전체에 적용된 사실의 기록이기 때문이다(Phase 9).
    /// </remarks>
    public MessageEnvelope AssembledEnvelope => new(
        _firstEnvelope.MessageId,
        _firstEnvelope.Flags & ~(FrameFlags.Fragmented | FrameFlags.EndOfMessage),
        _firstEnvelope.Sequence);

    /// <summary>재조립된 페이로드. <see cref="Reset"/> 전까지만 유효하다.</summary>
    public ReadOnlySequence<byte> AssembledPayload =>
        _buffer is null ? default : new ReadOnlySequence<byte>(_buffer, 0, _length);

    /// <summary>상태를 비우고 대여 버퍼를 반납한다.</summary>
    public void Reset()
    {
        byte[]? buffer = _buffer;
        _buffer = null;
        _length = 0;
        InProgress = false;
        _firstEnvelope = default;

        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void EnsureCapacity(int required)
    {
        if (_buffer is null)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(required, 4096));
            return;
        }

        if (_buffer.Length >= required)
        {
            return;
        }

        // 2배 성장 — 조각 수에 대해 복사 총량이 선형에 머문다. 상한 검사는 이미 끝났다.
        byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(required, _buffer.Length * 2));
        _buffer.AsSpan(0, _length).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }
}

/// <summary>조각 누적 실패의 원인. 전부 프로토콜 위반이다.</summary>
internal enum FragmentError
{
    /// <summary>실패 없음.</summary>
    None = 0,

    /// <summary>재조립 상한(<see cref="FramedConnectionOptions.MaxAssembledMessageLength"/>) 초과.</summary>
    TooLarge,

    /// <summary>조각 사이에 다른 메시지 식별자가 끼었다.</summary>
    MessageIdMismatch,
}
