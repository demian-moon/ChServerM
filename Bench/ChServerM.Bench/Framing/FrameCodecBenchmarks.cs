using System;
using System.Buffers;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using ChServerM.Framing;
using ChServerM.Identity;

namespace ChServerM.Bench.Framing;

/// <summary>
/// 프레임당 파싱 비용 (Phase 4 의 미측정 항목).
/// </summary>
/// <remarks>
/// <para>
/// <b>측정 목적.</b> 할당량이 0이라는 것은 이미 테스트로 확인했다
/// (<c>FrameCodecAllocationTests</c>). 여기서 얻으려는 것은 <b>시간</b>이다 —
/// "헤더 파싱 비용 0"이라고 ADR-0002 에 쓴 것이 실제로 무시할 수준인지 수치로 확인한다.
/// </para>
/// <para>
/// <b>세 경로를 나눠 잰다.</b>
/// </para>
/// <list type="number">
///   <item><description><b>빠른 경로</b> — 헤더가 첫 세그먼트 안에 다 있다. 복사 없음</description></item>
///   <item><description><b>느린 경로</b> — 헤더가 세그먼트에 걸쳐 있다. 16B <c>stackalloc</c> + 복사</description></item>
///   <item><description><b>부분 수신</b> — 프레임이 아직 안 왔다. 실전에서 가장 자주 도는 경로다</description></item>
/// </list>
/// <para>
/// 느린 경로가 빠른 경로보다 크게 느리면 세그먼트 경계가 흔한 워크로드에서 문제가 된다.
/// 그 차이를 모르면 어디를 고쳐야 할지 알 수 없다.
/// </para>
/// </remarks>
[Config(typeof(BenchConfig))]
public class FrameCodecBenchmarks
{
    private byte[] _frame = [];
    private ReadOnlySequence<byte> _single;
    private ReadOnlySequence<byte> _segmented;
    private ReadOnlySequence<byte> _partial;
    private FixedHeaderFrameDecoder _decoder = null!;
    private FixedHeaderFrameEncoder _encoder = null!;
    private ArrayBufferWriter<byte> _writer = null!;
    private FrameHeader _header;

    /// <summary>페이로드 크기. 헤더 파싱 비용이 페이로드 크기와 무관한지도 함께 확인한다.</summary>
    [Params(0, 64, 1024)]
    public int PayloadLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        FramingOptions options = new() { MaxPayloadLength = 64 * 1024 };
        _decoder = new FixedHeaderFrameDecoder(options);
        _encoder = new FixedHeaderFrameEncoder(options);
        _writer = new ArrayBufferWriter<byte>(FrameHeader.Size * 4);
        _header = _encoder.CreateHeader(new MessageId(1), PayloadLength, FrameFlags.None, 42);

        _frame = new byte[FrameHeader.Size + PayloadLength];
        FrameHeaderCodec.Write(_frame, _header);

        _single = new ReadOnlySequence<byte>(_frame);

        // 헤더가 반드시 두 세그먼트에 걸치도록 5바이트에서 자른다.
        _segmented = SegmentedSequence.Create(_frame, [5, _frame.Length - 5]);

        // 헤더는 왔지만 페이로드가 부족한 상태. 페이로드가 0이면 헤더 자체를 잘라야 한다.
        int available = PayloadLength == 0 ? FrameHeader.Size - 1 : FrameHeader.Size;
        _partial = new ReadOnlySequence<byte>(_frame, 0, available);
    }

    [Benchmark(Baseline = true, Description = "Decode 빠른 경로 (단일 세그먼트)")]
    public FrameDecodeStatus DecodeSingleSegment() => _decoder.Decode(_single).Status;

    [Benchmark(Description = "Decode 느린 경로 (세그먼트 경계)")]
    public FrameDecodeStatus DecodeAcrossSegments() => _decoder.Decode(_segmented).Status;

    [Benchmark(Description = "Decode 부분 수신 (NeedMoreData)")]
    public FrameDecodeStatus DecodePartial() => _decoder.Decode(_partial).Status;

    [Benchmark(Description = "헤더 쓰기 (WriteHeader)")]
    public int WriteHeader()
    {
        _writer.ResetWrittenCount();
        _encoder.WriteHeader(_writer, _header);
        return _writer.WrittenCount;
    }

    [Benchmark(Description = "코덱 왕복 (Write + TryRead)")]
    public FrameDecodeStatus CodecRoundTrip()
    {
        Span<byte> scratch = stackalloc byte[FrameHeader.Size];
        FrameHeaderCodec.Write(scratch, _header);
        return FrameHeaderCodec.TryRead(scratch, 64 * 1024, FrameHeader.CurrentVersion, out _);
    }
}

/// <summary>
/// 임의의 세그먼트 경계를 갖는 <see cref="ReadOnlySequence{T}"/>를 만든다.
/// </summary>
/// <remarks>
/// 테스트 프로젝트의 같은 헬퍼와 의도적으로 중복이다. 벤치마크가 테스트 어셈블리를
/// 참조하면 xUnit 이 측정 프로세스로 끌려 들어온다 — 그것이 측정에 무엇을 하는지
/// 알 수 없으므로 참조하지 않는다.
/// </remarks>
internal static class SegmentedSequence
{
    public static ReadOnlySequence<byte> Create(ReadOnlySpan<byte> data, IReadOnlyList<int> segmentSizes)
    {
        Segment? first = null;
        Segment? current = null;
        int offset = 0;

        foreach (int size in segmentSizes)
        {
            byte[] chunk = data.Slice(offset, size).ToArray();
            offset += size;

            if (first is null)
            {
                first = new Segment(chunk);
                current = first;
            }
            else
            {
                current = current!.Append(chunk);
            }
        }

        if (first is null || current is null)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        return new ReadOnlySequence<byte>(first, 0, current, current.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            Segment next = new(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
