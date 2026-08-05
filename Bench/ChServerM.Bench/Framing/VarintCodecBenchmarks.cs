using System.Buffers;
using BenchmarkDotNet.Attributes;
using ChServerM.Framing;
using ChServerM.Identity;

namespace ChServerM.Bench.Framing;

/// <summary>
/// varint 프레이밍의 프레임당 파싱 비용 — <see cref="FrameCodecBenchmarks"/>(고정 헤더)와
/// 같은 세 경로를 재서 두 프레이밍의 비용 차이를 비교 가능하게 한다.
/// </summary>
/// <remarks>
/// 도입 시 예상("varint 바이트 루프가 고정 헤더보다 느릴 것")은 2026-08-05 실측으로
/// <b>기각됐다</b> — ENV-B 에서 varint 디코드 16ns vs 고정 헤더 34ns. 고정 헤더의
/// 비용은 16B 읽기가 아니라 버전·플래그·예약 필드 검증이다(BENCHMARKS.md 해당 절).
/// 이 프레이밍의 존재 이유는 여전히 <b>작은 프레임의 헤더 오버헤드(2바이트 vs
/// 16바이트)</b>이고, 속도 불이익이 없음까지 확인됐다(ADR-0010).
/// </remarks>
[Config(typeof(BenchConfig))]
public class VarintCodecBenchmarks
{
    private byte[] _frame = [];
    private ReadOnlySequence<byte> _single;
    private ReadOnlySequence<byte> _segmented;
    private ReadOnlySequence<byte> _partial;
    private VarintFrameDecoder _decoder = null!;
    private VarintFrameEncoder _encoder = null!;
    private ArrayBufferWriter<byte> _writer = null!;
    private MessageEnvelope _envelope;

    /// <summary>페이로드 크기. varint 길이 필드의 바이트 수(1~2)가 갈리는 값들.</summary>
    [Params(0, 64, 1024)]
    public int PayloadLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _decoder = new VarintFrameDecoder(64 * 1024);
        _encoder = new VarintFrameEncoder(64 * 1024);
        _writer = new ArrayBufferWriter<byte>(64);
        _envelope = new MessageEnvelope(new MessageId(300), FrameFlags.None, 0);

        ArrayBufferWriter<byte> assembler = new();
        _encoder.WriteHeader(assembler, _envelope, PayloadLength);
        assembler.Write(new byte[PayloadLength]);
        _frame = assembler.WrittenSpan.ToArray();

        _single = new ReadOnlySequence<byte>(_frame);

        // 헤더(3~4바이트)가 반드시 세그먼트에 걸치도록 2바이트에서 자른다.
        _segmented = SegmentedSequence.Create(_frame, [2, _frame.Length - 2]);

        // 프레임이 아직 다 오지 않은 상태.
        _partial = new ReadOnlySequence<byte>(_frame, 0, _frame.Length - 1);
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
        _encoder.WriteHeader(_writer, _envelope, PayloadLength);
        return _writer.WrittenCount;
    }
}
