using System;
using System.Buffers;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Framing.Tests;

/// <summary>
/// "프레임당 힙 할당 0"은 Phase 1 의 합격 기준이다. 주장으로 두지 않고 측정한다.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>는 <b>현재 스레드</b>의 누적 할당만 센다.
/// 백그라운드 JIT 이나 다른 테스트의 할당이 섞이지 않는다.
/// </para>
/// <para>
/// 측정 루프 안에서는 단언하지 않는다 — xUnit 의 <c>Assert</c> 자체가 할당할 수 있다.
/// 결과는 필드에 누적해 JIT 이 루프를 통째로 제거하지 못하게 막는다.
/// </para>
/// <para>
/// 레거시는 패킷당 힙 할당이 5~8개였다. 초당 10만 패킷이면 GC 압력만으로 지연이 튄다.
/// </para>
/// </remarks>
public sealed class FrameCodecAllocationTests
{
    private const int Iterations = 10_000;
    private const int WarmupIterations = 1_000;

    /// <summary>JIT 이 결과 미사용을 이유로 루프를 지우지 못하게 붙잡는 싱크.</summary>
    private long _sink;

    private void Consume(in FrameDecodeResult result)
    {
        _sink += (int)result.Status + result.Payload.Length;
    }

    [Fact]
    public void Decode_SingleSegment_AllocatesNothing()
    {
        FixedHeaderFrameDecoder decoder = new(4096);
        byte[] frame = new byte[FrameHeader.Size + 512];
        FrameHeaderCodec.Write(frame, new FrameHeader(new MessageId(1), 512));
        ReadOnlySequence<byte> buffer = new(frame);

        for (int i = 0; i < WarmupIterations; i++)
        {
            Consume(decoder.Decode(buffer));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            Consume(decoder.Decode(buffer));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Decode_AcrossSegmentBoundary_AllocatesNothing()
    {
        // 느린 경로다. stackalloc 16바이트를 쓰므로 힙 할당이 없어야 한다.
        // 여기서 배열을 잡는 구현이면 경계를 넘는 프레임마다 GC 압력이 생긴다.
        FixedHeaderFrameDecoder decoder = new(4096);
        byte[] frame = new byte[FrameHeader.Size + 512];
        FrameHeaderCodec.Write(frame, new FrameHeader(new MessageId(1), 512));

        // 헤더가 반드시 두 세그먼트에 걸치도록 자른다.
        ReadOnlySequence<byte> buffer = SequenceFactory.Segmented(frame, [5, frame.Length - 5]);

        for (int i = 0; i < WarmupIterations; i++)
        {
            Consume(decoder.Decode(buffer));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            Consume(decoder.Decode(buffer));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Decode_NeedMoreData_AllocatesNothing()
    {
        // 부분 수신은 정상 경로이고 실제로는 완성된 프레임보다 자주 발생한다.
        FixedHeaderFrameDecoder decoder = new(4096);
        byte[] partial = new byte[FrameHeader.Size + 10];
        FrameHeaderCodec.Write(partial, new FrameHeader(new MessageId(1), 512));
        ReadOnlySequence<byte> buffer = new(partial);

        for (int i = 0; i < WarmupIterations; i++)
        {
            Consume(decoder.Decode(buffer));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            Consume(decoder.Decode(buffer));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WriteHeader_AllocatesNothing()
    {
        FixedHeaderFrameEncoder encoder = new(4096);
        ArrayBufferWriter<byte> writer = new(initialCapacity: 256);
        FrameHeader header = encoder.CreateHeader(new MessageId(1), 0, FrameFlags.Compressed, 7);

        for (int i = 0; i < WarmupIterations; i++)
        {
            writer.ResetWrittenCount();
            encoder.WriteHeader(writer, header);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            writer.ResetWrittenCount();
            encoder.WriteHeader(writer, header);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void HeaderCodec_WriteAndRead_AllocateNothing()
    {
        byte[] scratch = new byte[FrameHeader.Size];
        FrameHeader header = new(new MessageId(123), 456, FrameFlags.Encrypted, 789);
        long sink = 0;

        for (int i = 0; i < WarmupIterations; i++)
        {
            FrameHeaderCodec.Write(scratch, header);
            sink += FrameHeaderCodec.Read(scratch).PayloadLength;
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            FrameHeaderCodec.Write(scratch, header);
            sink += FrameHeaderCodec.Read(scratch).PayloadLength;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        _sink += sink;
        Assert.Equal(0, allocated);
    }

    // DCE 방지는 각 테스트가 힙 필드(_sink)에 쓰는 것으로 충분하다. 이것을 "검증"하는
    // 별도 테스트는 항상 참이라(xUnit 은 테스트마다 인스턴스를 새로 만들어 _sink 가 늘 0)
    // 삭제했다 (2026-08-04 감사).
}
