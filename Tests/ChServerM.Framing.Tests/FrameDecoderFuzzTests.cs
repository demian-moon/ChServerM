using System;
using System.Buffers;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Framing.Tests;

/// <summary>
/// 디코더는 <b>적대적 입력을 받는 첫 코드</b>다. 여기서 예외가 새거나 루프가 멈추지 않으면
/// 그것이 곧 서비스 거부 경로다.
/// </summary>
/// <remarks>
/// <para>검증하는 불변식은 넷이다.</para>
/// <list type="number">
///   <item><description><b>예외를 던지지 않는다.</b> 모든 실패는 상태값으로 나온다</description></item>
///   <item><description><b>버퍼 밖을 가리키지 않는다.</b> Decoded 면 페이로드가 버퍼 안에 있다</description></item>
///   <item><description><b>반드시 전진한다.</b> Decoded 인데 소비가 0이면 호출 루프가 영원히 돈다</description></item>
///   <item><description><b>NeedMoreData 는 버퍼 전체를 검사한다.</b> 아니면 파이프가 교착한다</description></item>
/// </list>
/// <para>
/// 시드는 고정한다. 실패를 재현할 수 없는 퍼징은 디버깅에 쓸모가 없다.
/// </para>
/// </remarks>
public sealed class FrameDecoderFuzzTests
{
    private const int MaxPayloadLength = 4096;
    private static readonly FixedHeaderFrameDecoder Decoder = new(MaxPayloadLength);

    /// <summary>한 번의 디코딩 결과가 모든 불변식을 지키는지 확인한다.</summary>
    private static void AssertInvariants(in ReadOnlySequence<byte> buffer, in FrameDecodeResult result)
    {
        switch (result.Status)
        {
            case FrameDecodeStatus.Decoded:
                // 페이로드가 버퍼 안에 있어야 한다.
                Assert.True(result.Payload.Length >= 0);
                Assert.True(result.Payload.Length <= buffer.Length - FrameHeader.Size);
                Assert.Equal(result.Header.PayloadLength, result.Payload.Length);

                // 반드시 전진한다. 최소 헤더 크기만큼은 소비해야 한다.
                long remaining = buffer.Slice(result.Consumed).Length;
                Assert.True(
                    remaining <= buffer.Length - FrameHeader.Size,
                    "Decoded 인데 소비가 헤더 크기에 못 미친다 — 호출 루프가 무한히 돈다.");
                break;

            case FrameDecodeStatus.NeedMoreData:
                // 버퍼 끝까지 검사했어야 파이프가 더 읽는다.
                Assert.Equal(0, buffer.Slice(result.Examined).Length);
                break;

            default:
                Assert.True(result.IsFatal);
                Assert.NotEqual(ChServerM.Diagnostics.ErrorCode.None, result.ToErrorCode());
                break;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(20260803)]
    public void Decode_RandomBytes_NeverThrows(int seed)
    {
        Random random = new(seed);

        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            byte[] noise = new byte[random.Next(0, 200)];
            random.NextBytes(noise);

            ReadOnlySequence<byte> buffer = new(noise);
            FrameDecodeResult result = Decoder.Decode(buffer);

            AssertInvariants(buffer, result);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public void Decode_RandomBytesAcrossRandomSegments_NeverThrows(int seed)
    {
        // 단일 세그먼트만 시험하면 stackalloc 느린 경로가 한 번도 안 돈다.
        Random random = new(seed);

        for (int iteration = 0; iteration < 5_000; iteration++)
        {
            byte[] noise = new byte[random.Next(1, 120)];
            random.NextBytes(noise);

            ReadOnlySequence<byte> buffer = SequenceFactory.Segmented(noise, random.Next(1, 8));
            FrameDecodeResult result = Decoder.Decode(buffer);

            AssertInvariants(buffer, result);
        }
    }

    [Fact]
    public void Decode_ValidFrameTruncatedAtEveryOffset_NeverThrows()
    {
        byte[] frame = new byte[FrameHeader.Size + 300];
        FrameHeaderCodec.Write(frame, new FrameHeader(new MessageId(5), 300));

        for (int length = 0; length <= frame.Length; length++)
        {
            ReadOnlySequence<byte> buffer = new(frame, 0, length);
            FrameDecodeResult result = Decoder.Decode(buffer);

            AssertInvariants(buffer, result);

            // 마지막 바이트가 오기 전에는 절대 성공하면 안 된다.
            Assert.Equal(length == frame.Length, result.IsDecoded);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(31)]
    public void Decode_BitFlippedValidFrame_NeverThrows(int seed)
    {
        // 전송 오류·능동적 변조를 흉내낸다.
        Random random = new(seed);
        byte[] pristine = new byte[FrameHeader.Size + 64];
        FrameHeaderCodec.Write(pristine, new FrameHeader(new MessageId(11), 64, FrameFlags.Encrypted, 3));

        for (int iteration = 0; iteration < 10_000; iteration++)
        {
            byte[] mutated = (byte[])pristine.Clone();
            int flips = random.Next(1, 5);
            for (int i = 0; i < flips; i++)
            {
                int position = random.Next(mutated.Length);
                mutated[position] ^= (byte)(1 << random.Next(8));
            }

            ReadOnlySequence<byte> buffer = new(mutated);
            AssertInvariants(buffer, Decoder.Decode(buffer));
        }
    }

    [Fact]
    public void Decode_AllPossibleLengthFieldValues_AreClassified()
    {
        // 길이 필드는 공격자가 완전히 통제하는 4바이트다.
        // 경계 주변과 극단값을 훑어 TooLarge 판정이 새지 않는지 본다.
        uint[] lengths =
        [
            0, 1,
            MaxPayloadLength - 1, MaxPayloadLength, MaxPayloadLength + 1,
            int.MaxValue - 1, int.MaxValue, (uint)int.MaxValue + 1,
            uint.MaxValue - 1, uint.MaxValue,
        ];

        byte[] header = new byte[FrameHeader.Size];

        foreach (uint length in lengths)
        {
            FrameHeaderCodec.Write(header, new FrameHeader(new MessageId(1), 0));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                header.AsSpan(FrameHeader.PayloadLengthOffset), length);

            ReadOnlySequence<byte> buffer = new(header);
            FrameDecodeResult result = Decoder.Decode(buffer);

            AssertInvariants(buffer, result);

            if (length > MaxPayloadLength)
            {
                Assert.Equal(FrameDecodeStatus.TooLarge, result.Status);
            }
        }
    }

    [Fact]
    public void Decode_RandomStreamOfFrames_AlwaysTerminates()
    {
        // 실제 사용 형태: 버퍼를 계속 슬라이스하며 프레임을 뽑는 루프.
        // 전진하지 않는 결과가 하나라도 있으면 이 테스트가 멈추지 않는다 → 반복 상한으로 잡는다.
        Random random = new(2026);
        ArrayBufferWriter<byte> writer = new();
        FixedHeaderFrameEncoder encoder = new(MaxPayloadLength);

        const int FrameCount = 500;
        for (int i = 0; i < FrameCount; i++)
        {
            int payloadLength = random.Next(0, 300);
            encoder.WriteHeader(writer, encoder.CreateHeader(
                new MessageId((ushort)(i % 1000 + 1)), payloadLength, FrameFlags.None, sequence: 0));
            writer.Write(new byte[payloadLength]);
        }

        ReadOnlySequence<byte> buffer = SequenceFactory.Segmented(writer.WrittenSpan, random.Next(1, 64));

        int decoded = 0;
        int iterations = 0;
        while (buffer.Length > 0)
        {
            Assert.True(++iterations <= FrameCount * 2, "디코딩 루프가 전진하지 않는다.");

            FrameDecodeResult result = Decoder.Decode(buffer);
            Assert.True(result.IsDecoded);

            decoded++;
            buffer = buffer.Slice(result.Consumed);
        }

        Assert.Equal(FrameCount, decoded);
    }
}
