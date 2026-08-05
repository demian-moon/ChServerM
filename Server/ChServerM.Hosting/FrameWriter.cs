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
/// <b>옵션 매개변수를 두지 않는다 — 전부 필수다.</b> 이 결정에는 이유가 있다.
/// 기본값을 줄 만한 세 인자가 하필 <b>레거시가 조용히 실패한 지점과 정확히 겹친다.</b>
/// </para>
/// <list type="table">
///   <item>
///     <term><c>cancellationToken</c></term>
///     <description>
///       기본값 <c>default</c> 는 "절대 취소되지 않음"을 뜻한다. 커넥션이 닫혔는데도
///       쓰기를 계속하게 되고, 그것이 레거시가 종료 후에도 소켓에 쓰던 경로다.
///       <see cref="IConnection.ConnectionClosed"/> 를 넘기도록 강제한다
///     </description>
///   </item>
///   <item>
///     <term><c>sequence</c></term>
///     <description>
///       기본값 0 은 엔벨로프의 <see cref="MessageEnvelope.Sequence"/> 필드를 무의미하게 만든다.
///       순서 진단과 Phase 9 리플레이 방지가 그 필드에 달려 있는데, 아무도 채우지 않으면
///       레거시의 "있는 척하는 체크섬 필드"와 같은 것이 된다
///     </description>
///   </item>
///   <item>
///     <term><c>flags</c></term>
///     <description>
///       기본값 <see cref="FrameFlags.None"/> 은 압축·암호화가 적용되지 않았다는 뜻이다.
///       나중에 압축 축을 꽂았을 때 이 기본값이 남아 있으면
///       <b>압축 코드가 한 번도 실행되지 않는다</b> — 레거시에서 실제로 일어난 일이다
///     </description>
///   </item>
/// </list>
/// <para>
/// 부수 효과로 <c>RS0026</c>(옵션 매개변수를 가진 오버로드 다중 정의)도 해소된다.
/// 공개 API 게이트가 이 문제를 처음 켠 날 잡아냈다.
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
    /// <param name="flags">적용된 변환. 변환이 없으면 <see cref="FrameFlags.None"/>.</param>
    /// <param name="sequence">프레임 일련번호.</param>
    /// <param name="cancellationToken">
    /// 내보내기 취소 토큰. 커넥션에 쓰는 경우 <see cref="IConnection.ConnectionClosed"/> 를 넘긴다.
    /// </param>
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
        FrameFlags flags,
        uint sequence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(encoder);

        encoder.WriteHeader(writer, new MessageEnvelope(messageId, flags, sequence), payload.Length);
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
        FrameFlags flags,
        uint sequence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, int.MaxValue);

        encoder.WriteHeader(writer, new MessageEnvelope(messageId, flags, sequence), (int)payload.Length);

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
    /// <b>이 오버로드의 존재 이유는 취소 토큰을 잊을 수 없게 하는 것이다.</b>
    /// <see cref="IConnection.ConnectionClosed"/> 를 자동으로 쓴다 —
    /// 커넥션이 닫히면 쓰기도 멈춰야 하고, 레거시는 그것을 하지 않아 종료 후에도
    /// 소켓에 쓰기를 시도했다.
    /// </remarks>
    public static ValueTask<FlushResult> WriteFrameAsync(
        this IConnection connection,
        IFrameEncoder encoder,
        Identity.MessageId messageId,
        ReadOnlySpan<byte> payload,
        FrameFlags flags,
        uint sequence)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return WriteFrameAsync(
            connection.Output, encoder, messageId, payload, flags, sequence, connection.ConnectionClosed);
    }

    /// <summary>큰 논리 메시지를 조각(<see cref="FrameFlags.Fragmented"/>)으로 나눠 보낸다.</summary>
    /// <param name="writer">출력 파이프.</param>
    /// <param name="encoder">헤더 인코더.</param>
    /// <param name="messageId">메시지 식별자. 모든 조각이 같은 값을 갖는다.</param>
    /// <param name="payload">논리 메시지 전체.</param>
    /// <param name="maxFragmentPayloadLength">조각 하나의 최대 페이로드.
    /// 프레이밍의 <c>MaxPayloadLength</c> 이하여야 한다.</param>
    /// <param name="flags">적용된 변환. 조각 플래그는 여기서 붙이므로 넣지 않는다.</param>
    /// <param name="sequence">첫 조각의 일련번호. 수신 측 재조립 엔벨로프에 실린다.</param>
    /// <param name="cancellationToken">내보내기 취소 토큰.</param>
    /// <returns>마지막 조각의 내보내기 결과.</returns>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/>일 때.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxFragmentPayloadLength"/>가 0 이하일 때.</exception>
    /// <exception cref="ArgumentException"><paramref name="flags"/>에 조각 플래그가 이미 들어 있을 때 —
    /// 중복 표시는 수신 측 계약 판정을 오염시키므로 조용히 합치지 않는다.</exception>
    /// <remarks>
    /// <para>
    /// 마지막 조각에만 <see cref="FrameFlags.EndOfMessage"/> 가 붙는다. 빈 페이로드는
    /// 조각 하나(<c>Fragmented|EndOfMessage</c>)로 나간다. 수신 측 계약(연속성·상한)은
    /// <see cref="FramedConnectionOptions.MaxAssembledMessageLength"/> 와 ADR-0015 참조.
    /// </para>
    /// <para>
    /// <b>조각 사이에 다른 프레임을 끼워 넣으면 안 된다</b> — 수신 측이 프로토콜 오류로
    /// 닫는다. 이 메서드가 도는 동안 같은 <paramref name="writer"/> 에 쓰지 않는 것은
    /// <see cref="PipeWriter"/> 단일 소유자 규약(모듈 문서)이 이미 요구하는 바다.
    /// </para>
    /// </remarks>
    public static async ValueTask<FlushResult> WriteFragmentedFrameAsync(
        PipeWriter writer,
        IFrameEncoder encoder,
        Identity.MessageId messageId,
        ReadOnlyMemory<byte> payload,
        int maxFragmentPayloadLength,
        FrameFlags flags,
        uint sequence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxFragmentPayloadLength, 0);

        if ((flags & (FrameFlags.Fragmented | FrameFlags.EndOfMessage)) != 0)
        {
            throw new ArgumentException(
                "조각 플래그는 이 메서드가 붙인다. flags 에 미리 넣지 않는다.", nameof(flags));
        }

        int offset = 0;
        FlushResult result;

        do
        {
            int chunk = Math.Min(maxFragmentPayloadLength, payload.Length - offset);
            bool last = offset + chunk >= payload.Length;

            FrameFlags fragmentFlags = flags | FrameFlags.Fragmented
                | (last ? FrameFlags.EndOfMessage : FrameFlags.None);

            result = await WriteFrameAsync(
                writer, encoder, messageId, payload.Span.Slice(offset, chunk),
                fragmentFlags, sequence, cancellationToken).ConfigureAwait(false);

            offset += chunk;

            if (result.IsCompleted || result.IsCanceled)
            {
                // 상대가 닫혔거나 취소됐다 — 나머지 조각을 계속 쓰는 것은 무의미하다.
                return result;
            }
        }
        while (offset < payload.Length);

        return result;
    }
}
