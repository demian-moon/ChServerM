using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Framing;

namespace ChServerM.Hosting;

/// <summary>
/// 헤더 + 페이로드를 한 프레임으로 써넣는다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 인코더는 헤더만 쓰고 페이로드는 호출자 몫이라(무의미한 복사를 피하려고)
/// 그 두 단계를 매번 손으로 엮으면 <b>길이를 잘못 넣는 실수</b>가 난다. 그 실수는
/// 수신 측 프레임 경계를 통째로 밀어버리고, 커넥션이 끊길 때까지 이어져 추적이 어렵다.
/// 여기서 길이를 페이로드에서 직접 계산해 그 실수를 구조적으로 없앤다.
/// </para>
/// <para>
/// <b>스레드 규약 — 중요.</b> <see cref="PipeWriter"/>는 동시 쓰기를 허용하지 않는다.
/// 하나의 커넥션에 여러 스레드가 응답을 쓰면 프레임이 뒤섞여 스트림이 손상된다.
/// 송신은 커넥션당 단일 소유자가 하거나, 상위에서 직렬화한다
/// (CLAUDE.md 9.1 "공유하지 않는 것이 1순위").
/// </para>
/// <para><b>할당.</b> 프레임당 힙 할당 0.</para>
/// </remarks>
public static class FrameWriter
{
    /// <summary>연속 메모리 페이로드로 프레임 하나를 쓰고 내보낸다.</summary>
    /// <param name="writer">출력 파이프.</param>
    /// <param name="encoder">헤더 인코더.</param>
    /// <param name="messageId">메시지 식별자.</param>
    /// <param name="payload">페이로드. 비어 있어도 된다.</param>
    /// <param name="flags">적용된 변환.</param>
    /// <param name="sequence">프레임 일련번호.</param>
    /// <param name="cancellationToken">내보내기 취소 토큰.</param>
    /// <returns>내보내기 결과.</returns>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/>일 때.</exception>
    /// <remarks>
    /// 길이는 <paramref name="payload"/>에서 계산한다. 호출자가 헤더에 손으로 넣지 않는다.
    /// </remarks>
    public static ValueTask<FlushResult> WriteFrameAsync(
        PipeWriter writer,
        IFrameEncoder encoder,
        Identity.MessageId messageId,
        ReadOnlySpan<byte> payload,
        FrameFlags flags = FrameFlags.None,
        uint sequence = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(encoder);

        encoder.WriteHeader(writer, new FrameHeader(messageId, payload.Length, flags, sequence));
        writer.Write(payload);

        return writer.FlushAsync(cancellationToken);
    }

    /// <summary>분절된 페이로드로 프레임 하나를 쓰고 내보낸다.</summary>
    /// <param name="writer">출력 파이프.</param>
    /// <param name="encoder">헤더 인코더.</param>
    /// <param name="messageId">메시지 식별자.</param>
    /// <param name="payload">페이로드. 세그먼트가 여러 개여도 된다.</param>
    /// <param name="flags">적용된 변환.</param>
    /// <param name="sequence">프레임 일련번호.</param>
    /// <param name="cancellationToken">내보내기 취소 토큰.</param>
    /// <returns>내보내기 결과.</returns>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/>일 때.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="payload"/>가 <see cref="int"/> 범위를 넘을 때.
    /// </exception>
    /// <remarks>
    /// 받은 시퀀스를 <b>그대로 세그먼트 단위로</b> 흘려보낸다.
    /// <c>ToArray()</c> 로 평탄화하지 않는다 — 그 복사가 제로 카피를 무너뜨린다.
    /// 에코나 릴레이처럼 수신 버퍼를 그대로 되돌려 보낼 때 쓴다.
    /// </remarks>
    public static ValueTask<FlushResult> WriteFrameAsync(
        PipeWriter writer,
        IFrameEncoder encoder,
        Identity.MessageId messageId,
        in ReadOnlySequence<byte> payload,
        FrameFlags flags = FrameFlags.None,
        uint sequence = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, int.MaxValue);

        encoder.WriteHeader(writer, new FrameHeader(messageId, (int)payload.Length, flags, sequence));

        foreach (ReadOnlyMemory<byte> segment in payload)
        {
            writer.Write(segment.Span);
        }

        return writer.FlushAsync(cancellationToken);
    }

    /// <summary>커넥션에 프레임 하나를 쓰고 내보낸다.</summary>
    /// <param name="connection">대상 커넥션.</param>
    /// <param name="encoder">헤더 인코더.</param>
    /// <param name="messageId">메시지 식별자.</param>
    /// <param name="payload">페이로드.</param>
    /// <param name="flags">적용된 변환.</param>
    /// <param name="sequence">프레임 일련번호.</param>
    /// <returns>내보내기 결과.</returns>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/>일 때.</exception>
    /// <remarks>
    /// 취소는 <see cref="IConnection.ConnectionClosed"/>를 쓴다 — 커넥션이 닫히면
    /// 쓰기도 멈춰야 한다. 레거시는 종료 후에도 쓰기를 시도했다.
    /// </remarks>
    public static ValueTask<FlushResult> WriteFrameAsync(
        this IConnection connection,
        IFrameEncoder encoder,
        Identity.MessageId messageId,
        ReadOnlySpan<byte> payload,
        FrameFlags flags = FrameFlags.None,
        uint sequence = 0)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return WriteFrameAsync(
            connection.Output, encoder, messageId, payload, flags, sequence, connection.ConnectionClosed);
    }
}
