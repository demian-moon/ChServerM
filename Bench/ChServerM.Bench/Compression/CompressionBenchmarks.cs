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
/// 페이로드 2종: 압축성(반복 구조 — 게임 상태·좌표 스냅샷 류) / 비압축성(결정적
/// 의사난수 — 암호화·재압축된 데이터 류). 비압축성 채움은 <c>System.Random</c> 이 아니라
/// 곱셈 해시로 만든다 — 벤치 재현성(고정 시드)과 분석기 규약(CA5394)을 동시에 지킨다.
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

        // 결정적 비압축성 채움 — 곱셈 해시(splitmix 계열)로 엔트로피를 채운다.
        _incompressible = new byte[PayloadLength];
        ulong state = 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < _incompressible.Length; i++)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            _incompressible[i] = (byte)(z >> 56);
        }

        _lz4Output = new byte[_lz4.MaxEncodedLength(PayloadLength)];
        _brotliOutput = new byte[BrotliEncoder.GetMaxCompressedLength(PayloadLength)];
        _decodeOutput = new byte[PayloadLength];

        _lz4CompressedBlob = new byte[_lz4.MaxEncodedLength(PayloadLength)];
        int encoded = _lz4.Encode(_compressible, _lz4CompressedBlob);
        Array.Resize(ref _lz4CompressedBlob, encoded);
    }

    [Benchmark(Baseline = true, Description = "LZ4 인코드 (압축성)")]
    public int Lz4EncodeCompressible() => _lz4.Encode(_compressible, _lz4Output);

    [Benchmark(Description = "LZ4 인코드 (비압축성)")]
    public int Lz4EncodeIncompressible() => _lz4.Encode(_incompressible, _lz4Output);

    [Benchmark(Description = "Brotli 인코드 (압축성)")]
    public int BrotliEncodeCompressible()
    {
        BrotliEncoder.TryCompress(_compressible, _brotliOutput, out int written, quality: 1, window: 22);
        return written;
    }

    [Benchmark(Description = "Brotli 인코드 (비압축성)")]
    public int BrotliEncodeIncompressible()
    {
        BrotliEncoder.TryCompress(_incompressible, _brotliOutput, out int written, quality: 1, window: 22);
        return written;
    }

    [Benchmark(Description = "LZ4 디코드 (압축성)")]
    public int Lz4DecodeCompressible()
    {
        System.Buffers.ArrayBufferWriter<byte> writer = new(_decodeOutput.Length);
        _lz4.TryDecode(
            new System.Buffers.ReadOnlySequence<byte>(_lz4CompressedBlob), writer, PayloadLength, out int decoded);
        return decoded;
    }
}
