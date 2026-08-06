using System;
using System.Buffers;
using ChServerM.Compression.LZ4;
using Xunit;

namespace ChServerM.Compression.LZ4.Tests;

/// <summary>
/// LZ4 해제 경로의 퍼징 불변식 — 압축 블롭은 순수 원격 입력이므로
/// <b>어떤 바이트에도 던지지 않고</b>, 출력은 상한을 넘지 않으며, 실패 시 아무것도
/// 커밋되지 않아야 한다(T-16·T-18).
/// </summary>
/// <remarks>시드 고정 — 실패가 나면 같은 시드로 재현된다.</remarks>
public sealed class Lz4PayloadCodecFuzzTests
{
    private const int Iterations = 5_000;
    private const int Seed = 20260806;
    private const int MaxDecoded = 8 * 1024;

    private readonly Lz4PayloadCodec _codec = new();

    [Fact]
    public void Random_blobs_never_throw_and_respect_output_cap()
    {
        Random random = new(Seed);
        byte[] blob = new byte[4 * 1024];

        for (int i = 0; i < Iterations; i++)
        {
            int length = random.Next(0, blob.Length + 1);
            random.NextBytes(blob.AsSpan(0, length));

            ArrayBufferWriter<byte> output = new();
            bool decodeOk = _codec.TryDecode(
                new ReadOnlySequence<byte>(blob.AsMemory(0, length)), output, MaxDecoded, out int decodedLength);

            if (decodeOk)
            {
                // 무작위가 우연히 유효해도 상한 계약은 지켜져야 한다.
                Assert.InRange(decodedLength, 0, MaxDecoded);
                Assert.Equal(decodedLength, output.WrittenCount);
            }
            else
            {
                // 실패는 커밋 없이 — 부분 출력이 남으면 호출자가 쓰레기를 디스패치한다.
                Assert.Equal(0, decodedLength);
                Assert.Equal(0, output.WrittenCount);
            }
        }
    }

    [Fact]
    public void Single_byte_mutations_of_valid_blob_never_throw_and_never_overflow()
    {
        byte[] original = new byte[2 * 1024];
        for (int i = 0; i < original.Length; i++)
        {
            original[i] = (byte)(i % 16);
        }

        byte[] valid = new byte[_codec.MaxEncodedLength(original.Length)];
        int encoded = _codec.Encode(original, valid);
        byte[] mutated = new byte[encoded];

        for (int offset = 0; offset < encoded; offset++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                valid.AsSpan(0, encoded).CopyTo(mutated);
                mutated[offset] ^= (byte)(1 << bit);

                ArrayBufferWriter<byte> output = new();
                bool decodeOk = _codec.TryDecode(
                    new ReadOnlySequence<byte>(mutated), output, MaxDecoded, out int decodedLength);

                // 불변식: 던지지 않고, 성공하든 실패하든 출력이 상한·커밋 계약을 지킨다.
                // (LZ4 블록엔 무결성이 없어 내용 손상은 "성공"할 수 있다 — 그것은 AEAD 의 몫.)
                if (decodeOk)
                {
                    Assert.InRange(decodedLength, 0, MaxDecoded);
                    Assert.Equal(decodedLength, output.WrittenCount);
                }
                else
                {
                    Assert.Equal(0, output.WrittenCount);
                }
            }
        }
    }

    [Fact]
    public void Length_prefix_extremes_never_throw_and_never_allocate_output()
    {
        // 길이 접두 4바이트의 극단값 전수 — T-12/T-18 의 공격 지점.
        uint[] extremes = [0, 1, (uint)MaxDecoded, (uint)MaxDecoded + 1, int.MaxValue, uint.MaxValue];
        byte[] blob = new byte[64];

        foreach (uint claimed in extremes)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(blob, claimed);

            ArrayBufferWriter<byte> output = new();
            bool decodeOk = _codec.TryDecode(
                new ReadOnlySequence<byte>(blob), output, MaxDecoded, out _);

            if (claimed > MaxDecoded)
            {
                Assert.False(decodeOk);
                Assert.Equal(0, output.WrittenCount);
            }
        }
    }
}
