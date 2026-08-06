using System;
using System.IO.Compression;
using BenchmarkDotNet.Attributes;
using ChServerM.Compression.LZ4;

namespace ChServerM.Bench.Compression;

/// <summary>
/// 압축 축 비교 — LZ4(채택, ADR-0019) vs Brotli(BCL, 의존 0 대안).
/// </summary>
/// <remarks>
/// <para>
/// <b>무엇을 판정하는가.</b> (1) "실시간 메시지 압축엔 LZ4" 가설의 수치 근거,
/// (2) 압축 문턱 기본값(1024B)의 타당성 — 작은 페이로드에서 압축 비용 대비 이득이
/// 있는가. 결과는 <c>docs/BENCHMARKS.md</c> 압축 절에 기록한다.
/// </para>
/// <para>
/// 페이로드 2종: 압축성(반복 구조 — 게임 상태·좌표 스냅샷 류) / 비압축성(랜덤 —
/// 암호화·재압축된 데이터 류). 실측 워크로드가 생기면 대표 페이로드를 교체한다.
/// </para>
/// <para>Brotli 는 BCL <see cref="BrotliEncoder"/> 원시 API 를 품질 1(최속)로 쓴다 —
/// 비교군에게 최대한 유리한 조건이다(벤치 대결은 불리한 쪽에 기울여 설계한다).</para>
/// </remarks>
[MemoryDiagnoser]
public class CompressionBenchmarks
{
    private Lz4PayloadCodec _lz4 = null!;
    private byte[] _compressible = null!;
    private byte[] _incompressible = null!;
    private byte[] _lz4Output = null!;
    private byte[] _brotliOutput = null!;
    private byte[] _lz4CompressedBlob = null!;
    private byte[] _decodeOutput = null!;

    /// <summary>페이로드 크기. 1024 는 압축 문턱 기본값의 검증 지점이다.</summary>
    [Params(1024, 16 * 1024)]
    public int PayloadLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _lz4 = new Lz4PayloadCodec();

        _compressible = new byte[PayloadLength];
        for (int i = 0; i < _compressible.Length; i++)
        {
            _compressible[i] = (byte)(i % 16);
        }

        _incompressible = new byte[PayloadLength];
        new Random(42).NextBytes(_incompressible);

        _lz4Output = new byte[_lz4.MaxEncodedLength(PayloadLength)];
        _brotliOutput = new byte[BrotliEncoder.GetMaxCompressedLength(PayloadLength)];
        _decodeOutput = new byte[PayloadLength];

        int encoded = _lz4.Encode(_compressible, _lz4CompressedBlob = new byte[_lz4.MaxEncodedLength(PayloadLength)]);
        Array.Resize(ref _lz4CompressedBlob, encoded);
    }

    [Benchmark(Baseline = true)]
    public int Lz4_Encode_Compressible() => _lz4.Encode(_compressible, _lz4Output);

    [Benchmark]
    public int Lz4_Encode_Incompressible() => _lz4.Encode(_incompressible, _lz4Output);

    [Benchmark]
    public int Brotli_Encode_Compressible()
    {
        BrotliEncoder.TryCompress(_compressible, _brotliOutput, out int written, quality: 1, window: 22);
        return written;
    }

    [Benchmark]
    public int Brotli_Encode_Incompressible()
    {
        BrotliEncoder.TryCompress(_incompressible, _brotliOutput, out int written, quality: 1, window: 22);
        return written;
    }

    [Benchmark]
    public int Lz4_Decode_Compressible()
    {
        System.Buffers.ArrayBufferWriter<byte> writer = new(_decodeOutput.Length);
        _lz4.TryDecode(
            new System.Buffers.ReadOnlySequence<byte>(_lz4CompressedBlob), writer, PayloadLength, out int decoded);
        return decoded;
    }
}
