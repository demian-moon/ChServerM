using System;
using System.Buffers;
using FlatSharp;

namespace ChServerM.Serialization.FlatBuffers;

/// <summary>
/// FlatSharp(FlatBuffers) 으로 메시지를 직렬화·역직렬화한다.
/// </summary>
/// <typeparam name="TMessage">FlatSharp.Compiler 가 생성한 테이블 타입.</typeparam>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 직렬화 축의 세 번째 어댑터다(ADR-0012). 레거시가 FlatBuffers
/// 스키마·생성 코드를 운영 중이므로 승계 경로의 실물이고, 4자 벤치마크의 비교군이다.
/// </para>
/// <para>
/// <b>Greedy 전용.</b> <see cref="IMessageSerializer{TMessage}"/> 계약은 "호출이 끝나면
/// 페이로드는 무효"다. FlatSharp 의 Lazy/Progressive 역직렬화는 반환된 객체가 버퍼를
/// 계속 참조하므로 이 계약과 <b>양립할 수 없다</b> — 생성자가 Greedy 계열이 아닌
/// 직렬화기를 조립 시점에 거부한다. FlatBuffers 의 간판 기능(역직렬화 없는 랜덤 접근)을
/// 버리는 결정이며, 그 기능을 살리려면 계약에 lazy 접근 축이 필요하다 —
/// 4자 벤치마크 결과와 함께 판단한다(ADR-0012).
/// </para>
/// <para>
/// <b>포맷 특성 — 자기 검증 부재.</b> FlatBuffers 는 오프셋 기반 포맷이라 임의
/// 바이트열 검증을 제공하지 않는다. 손상 입력은 예외로 드러나는 경우만 실패로
/// 판정할 수 있고, 우연히 유효한 오프셋이면 쓰레기 값이 나온다. 신뢰 경계 밖
/// 입력에는 Phase 9 AEAD 통과 후에만 쓴다는 프레임워크 전제가 여기서는 필수다.
/// </para>
/// <para>
/// <b>다중 세그먼트는 복사 경로다.</b> FlatSharp 파서는 연속 메모리를 요구한다.
/// 세그먼트를 넘는 페이로드는 풀 대여 버퍼로 복사하고 <c>finally</c> 로 반납한다
/// (레거시 ArrayPool 미반납 재발 방지).
/// </para>
/// <para><b>스레드 규약.</b> 생성 후 상태가 불변이라 스레드 안전하다.</para>
/// </remarks>
public sealed class FlatSharpMessageSerializer<TMessage> : IMessageSerializer<TMessage>
    where TMessage : class
{
    private readonly ISerializer<TMessage> _serializer;

    /// <summary>생성 코드의 직렬화기를 감싼다.</summary>
    /// <param name="serializer">FlatSharp 생성 직렬화기 (예: <c>TMessage.Serializer</c>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="serializer"/> 가
    /// <see langword="null"/> 일 때.</exception>
    /// <exception cref="ArgumentException">역직렬화 옵션이 Greedy 계열이 아닐 때.
    /// Lazy/Progressive 는 반환 객체가 페이로드 버퍼를 참조하므로 계약 위반이다 —
    /// 조립 시점에 거부한다.</exception>
    public FlatSharpMessageSerializer(ISerializer<TMessage> serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        if (serializer.DeserializationOption is not
            (FlatBufferDeserializationOption.Greedy or FlatBufferDeserializationOption.GreedyMutable))
        {
            throw new ArgumentException(
                $"역직렬화 옵션이 {serializer.DeserializationOption} 이다. " +
                "Lazy/Progressive 는 반환 객체가 페이로드 버퍼를 참조하므로 " +
                "IMessageSerializer 계약(호출 후 페이로드 무효)과 양립할 수 없다. " +
                "스키마에 (fs_serializer:\"Greedy\") 를 지정하라.",
                nameof(serializer));
        }

        _serializer = serializer;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> 또는
    /// <paramref name="message"/> 가 <see langword="null"/> 일 때.</exception>
    public void Serialize(IBufferWriter<byte> writer, in TMessage message)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(message);

        // GetMaxSize 는 상한이다 — 실제 크기는 Write 가 돌려준다.
        int maxSize = _serializer.GetMaxSize(message);
        Span<byte> destination = writer.GetSpan(maxSize);
        int written = _serializer.Write(destination, message);
        writer.Advance(written);
    }

    /// <inheritdoc/>
    public bool TryDeserialize(in ReadOnlySequence<byte> payload, out TMessage message)
    {
        if (payload.Length > int.MaxValue)
        {
            message = null!;
            return false;
        }

        if (payload.IsSingleSegment)
        {
            return TryParse(payload.First, out message);
        }

        // FlatSharp 는 연속 메모리를 요구한다. 분절 페이로드는 복사가 불가피하다.
        byte[] rented = ArrayPool<byte>.Shared.Rent((int)payload.Length);

        try
        {
            payload.CopyTo(rented);
            // Greedy 파싱이라 반환 객체는 rented 를 참조하지 않는다 — 반납해도 안전하다.
            return TryParse(rented.AsMemory(0, (int)payload.Length), out message);
        }
        finally
        {
            // 반납을 finally 에 둔다. 레거시의 ArrayPool 미반납이 여기서 재발하지 않게.
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private bool TryParse(ReadOnlyMemory<byte> buffer, out TMessage message)
    {
        try
        {
            message = _serializer.Parse(buffer);
            return true;
        }
#pragma warning disable CA1031 // 포맷이 자기 검증을 제공하지 않아 실패 예외 타입이 특정되지 않는다.
        catch (Exception)
#pragma warning restore CA1031
        {
            // 손상된 페이로드는 정상적인 입력의 일부다(버그이거나 공격이다).
            // FlatBuffers 는 검증 계층이 없어 손상이 어떤 예외로 드러날지 포맷이
            // 정의하지 않는다 — 전부 실패 판정으로 수렴시키는 것이 계약이다.
            message = null!;
            return false;
        }
    }
}
