using System;
using System.Buffers;
using System.Buffers.Binary;
using ChServerM.Compression.LZ4;
using Xunit;

namespace ChServerM.Compression.LZ4.Tests;

/// <summary>
/// LZ4 어댑터의 계약 — 왕복, "압축이 실제로 실행됨"(레거시 무동작의 역),
/// 그리고 T-18(폭탄)·T-12(선언값 신뢰)의 방어선을 고정한다.
/// </summary>
public sealed class Lz4PayloadCodecTests
{
    private readonly Lz4PayloadCodec _codec = new();

    /// <summary>압축이 잘 되는 대표 페이로드 — 반복 구조(게임 상태·JSON 류).</summary>
    private static byte[] Compressible(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(i % 16);
        }

        return data;
    }

    private byte[] EncodeToArray(ReadOnlySpan<byte> source)
    {
        byte[] buffer = new byte[_codec.MaxEncodedLength(source.Length)];
        int written = _codec.Encode(source, buffer);
        return buffer.AsSpan(0, written).ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(64 * 1024)]
    public void RoundTrip_RestoresOriginal(int length)
    {
        byte[] original = Compressible(length);
        byte[] blob = EncodeToArray(original);

        ArrayBufferWriter<byte> output = new();
        bool decodeOk = _codec.TryDecode(
            new ReadOnlySequence<byte>(blob), output, maxDecodedLength: length, out int decodedLength);

        Assert.True(decodeOk);
        Assert.Equal(length, decodedLength);
        Assert.Equal(original, output.WrittenSpan.ToArray());
    }

    [Fact]
    public void Compression_ActuallyShrinks_CompressibleData()
    {
        // 레거시는 압축이 한 번도 실행되지 않았다(maxLength >= originDataLen 항상 참).
        // "실제로 줄어든다"가 이 어댑터의 존재 증명이다.
        byte[] original = Compressible(16 * 1024);
        byte[] blob = EncodeToArray(original);

        Assert.True(
            blob.Length < original.Length,
            $"압축 결과({blob.Length}B)가 원본({original.Length}B)보다 작지 않다.");
    }

    [Fact]
    public void RandomData_MayExpand_ButStillRoundTrips()
    {
        // 비압축성 데이터 — 커질 수 있고(평문 송신 판정은 송신 정책의 몫), 왕복은 보장된다.
        byte[] original = new byte[4096];
        Random.Shared.NextBytes(original);
        byte[] blob = EncodeToArray(original);

        ArrayBufferWriter<byte> output = new();
        Assert.True(_codec.TryDecode(new ReadOnlySequence<byte>(blob), output, original.Length, out _));
        Assert.Equal(original, output.WrittenSpan.ToArray());
    }

    [Fact]
    public void RoundTrip_AcrossSegmentBoundaries()
    {
        // 파이프는 블롭을 세그먼트 경계에서 자를 수 있다 — 단일 세그먼트 가정 금지.
        byte[] original = Compressible(8 * 1024);
        byte[] blob = EncodeToArray(original);

        foreach (int split in new[] { 1, 3, 4, 5, blob.Length / 2, blob.Length - 1 })
        {
            ReadOnlySequence<byte> segmented = Segmented(blob, split);
            ArrayBufferWriter<byte> output = new();

            Assert.True(_codec.TryDecode(segmented, output, original.Length, out int decodedLength));
            Assert.Equal(original.Length, decodedLength);
            Assert.Equal(original, output.WrittenSpan.ToArray());
        }
    }

    // ── T-18 압축 폭탄 · T-12 선언값 신뢰 ────────────────────────

    [Fact]
    public void Bomb_ClaimingAboveLimit_IsRejectedWithoutWriting()
    {
        byte[] original = Compressible(4096);
        byte[] blob = EncodeToArray(original);

        // 상한이 선언보다 1 작으면 — 버퍼를 잡기 전에 거부돼야 한다.
        ArrayBufferWriter<byte> output = new();
        bool decodeOk = _codec.TryDecode(
            new ReadOnlySequence<byte>(blob), output, maxDecodedLength: 4095, out int decodedLength);

        Assert.False(decodeOk);
        Assert.Equal(0, decodedLength);
        Assert.Equal(0, output.WrittenCount);
    }

    [Fact]
    public void Bomb_HugeClaimedLength_IsRejected()
    {
        // 4바이트짜리 거짓말 — 선언 길이 1GiB. 할당 없이 거부돼야 한다.
        byte[] blob = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(blob, 1024u * 1024 * 1024);

        ArrayBufferWriter<byte> output = new();
        Assert.False(_codec.TryDecode(new ReadOnlySequence<byte>(blob), output, 1024 * 1024, out _));
        Assert.Equal(0, output.WrittenCount);
    }

    [Fact]
    public void StructurallyCorruptedBlock_FailsAsValue()
    {
        // 블록을 잘라 구조를 깨뜨린다 — 해제 결과가 선언 길이와 달라져 거부된다.
        // ⚠ LZ4 블록에는 무결성 검사가 없다: 길이가 변하지 않는 내용 손상(리터럴 비트
        // 뒤집기)은 여기서 잡히지 않는다. 무결성은 전송 보안 축의 AEAD 가 담당한다(T-04) —
        // 이 코덱에 가짜 무결성 장치를 만들지 않는 것이 정직하다(레거시 가짜 체크섬의 역).
        byte[] original = Compressible(4096);
        byte[] blob = EncodeToArray(original);
        byte[] truncated = blob.AsSpan(0, blob.Length - 8).ToArray();

        ArrayBufferWriter<byte> output = new();
        Assert.False(_codec.TryDecode(new ReadOnlySequence<byte>(truncated), output, original.Length, out _));
        Assert.Equal(0, output.WrittenCount);
    }

    [Fact]
    public void ClaimedLengthMismatch_IsRejected()
    {
        // 선언 길이를 조작 — 실제 해제 결과와 다르면 커밋되지 않아야 한다.
        byte[] original = Compressible(4096);
        byte[] blob = EncodeToArray(original);
        BinaryPrimitives.WriteUInt32LittleEndian(blob, 4000u); // 실제는 4096

        ArrayBufferWriter<byte> output = new();
        Assert.False(_codec.TryDecode(new ReadOnlySequence<byte>(blob), output, 4096, out _));
        Assert.Equal(0, output.WrittenCount);
    }

    [Fact]
    public void TruncatedBlob_IsRejected()
    {
        Assert.False(_codec.TryDecode(
            new ReadOnlySequence<byte>(new byte[3]), new ArrayBufferWriter<byte>(), 1024, out _));

        // 길이만 있고 블록이 없는데 선언은 0이 아니다.
        byte[] headerOnly = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(headerOnly, 100u);
        Assert.False(_codec.TryDecode(
            new ReadOnlySequence<byte>(headerOnly), new ArrayBufferWriter<byte>(), 1024, out _));
    }

    [Fact]
    public void EmptyPayloadBlob_RoundTrips_AndRejectsTrailingBytes()
    {
        byte[] blob = EncodeToArray(ReadOnlySpan<byte>.Empty);
        Assert.Equal(Lz4PayloadCodec.HeaderSize, blob.Length);

        ArrayBufferWriter<byte> output = new();
        Assert.True(_codec.TryDecode(new ReadOnlySequence<byte>(blob), output, 0, out int decodedLength));
        Assert.Equal(0, decodedLength);

        // 선언 0 인데 블록이 붙어 있으면 형식 위반이다.
        byte[] trailing = new byte[5];
        Assert.False(_codec.TryDecode(new ReadOnlySequence<byte>(trailing), output, 1024, out _));
    }

    // ── 호출자 버그 가드 ─────────────────────────────────────────

    [Fact]
    public void Encode_RejectsShortDestination()
    {
        Assert.Throws<ArgumentException>(() => _codec.Encode(Compressible(1024), new byte[8]));
    }

    [Fact]
    public void MaxEncodedLength_RejectsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _codec.MaxEncodedLength(-1));
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────

    private static ReadOnlySequence<byte> Segmented(byte[] data, int splitAt)
    {
        TestSegment first = new(data.AsMemory(0, splitAt), 0);
        TestSegment second = first.Append(data.AsMemory(splitAt));
        return new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);
    }

    private sealed class TestSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSegment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public TestSegment Append(ReadOnlyMemory<byte> memory)
        {
            TestSegment next = new(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
