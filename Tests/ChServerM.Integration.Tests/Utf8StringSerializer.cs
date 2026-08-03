using System;
using System.Buffers;
using System.Text;
using ChServerM.Serialization;

namespace ChServerM.Integration.Tests;

/// <summary>
/// UTF-8 문자열 직렬화기. 직렬화 축이 실제로 꽂히는지 확인하기 위한 최소 구현이다.
/// </summary>
/// <remarks>
/// <para>
/// <b>이것은 프로덕션 직렬화기가 아니다.</b> FlatBuffers 구현체는 Phase 6 벤치마크로
/// 정한다(ADR-0002 미결 항목). 여기서 증명하려는 것은 하나다 —
/// <see cref="IMessageSerializer{TMessage}"/> 계약만으로 디스패처에 타입 있는 핸들러를
/// 붙일 수 있는가.
/// </para>
/// <para>
/// <see cref="ReadOnlySequence{T}"/>를 <b>평탄화하지 않고</b> 다룬다. 세그먼트가 여러 개인
/// 페이로드에서 <c>ToArray()</c> 를 부르는 순간 제로 카피가 무너진다 —
/// 진짜 직렬화기도 이 규약을 지켜야 하므로 여기서부터 지킨다.
/// </para>
/// <para><b>스레드 규약.</b> 상태가 없어 스레드 안전하다.</para>
/// </remarks>
internal sealed class Utf8StringSerializer : IMessageSerializer<string>
{
    public static Utf8StringSerializer Instance { get; } = new();

    public void Serialize(IBufferWriter<byte> writer, in string message)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        Encoding.UTF8.GetBytes(message.AsSpan(), writer);
    }

    public bool TryDeserialize(in ReadOnlySequence<byte> payload, out string message)
    {
        if (payload.Length > int.MaxValue)
        {
            message = string.Empty;
            return false;
        }

        try
        {
            // GetString(ReadOnlySequence) 오버로드가 세그먼트를 알아서 다룬다.
            message = payload.IsSingleSegment
                ? Encoding.UTF8.GetString(payload.FirstSpan)
                : DecodeMultiSegment(payload);

            return true;
        }
        catch (DecoderFallbackException)
        {
            // 손상된 UTF-8 은 정상적인 입력의 일부다(버그이거나 공격이다).
            // 예외를 밖으로 흘리면 그 자체가 서비스 거부 경로가 된다.
            message = string.Empty;
            return false;
        }
    }

    private static string DecodeMultiSegment(in ReadOnlySequence<byte> payload)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent((int)payload.Length);

        try
        {
            payload.CopyTo(rented);
            return Encoding.UTF8.GetString(rented.AsSpan(0, (int)payload.Length));
        }
        finally
        {
            // 반납을 finally 에 둔다. 레거시의 ArrayPool 미반납이 여기서 재발하지 않게.
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
