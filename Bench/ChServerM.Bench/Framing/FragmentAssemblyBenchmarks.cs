using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Hosting.Dispatch;
using ChServerM.Identity;

namespace ChServerM.Bench.Framing;

/// <summary>
/// <b>조각 재조립의 조건부 비용</b> — 조각을 안 쓰는 커넥션은 정말 0 을 내는가,
/// 조각을 쓰는 메시지는 얼마를 내는가 (Phase 15 잔여 관찰 항목, ADR-0015).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <c>FramedConnectionHandler</c> 는 재조립기(<c>FragmentAssembler</c>)를
/// <b>첫 조각이 올 때</b> 만들고, 메시지 완성 즉시 대여 버퍼를 반납한다 — "조각을 안 쓰는
/// 커넥션은 비용 0" 이 ADR-0015 의 주장이다. 주장의 양쪽(0 인 쪽과 0 이 아닌 쪽의 크기)을
/// 수치로 닫는다. 재조립기는 <c>internal</c> 이므로 <b>공개 표면(읽기 루프 전체)</b>을
/// 통해 잰다 — 실제 지불 지점이 그 경로이기도 하다.
/// </para>
/// <para>
/// <b>측정 구조.</b> 수명이 긴 커넥션 하나에 읽기 루프를 걸어 두고, 벤치 연산마다
/// 미리 인코딩한 프레임 블롭(메시지 128개분)을 입력 파이프에 밀어 넣은 뒤 무동작 핸들러의
/// 처리 계수가 따라올 때까지 스핀 대기한다. 세 팔 모두 <b>논리 메시지당 4 KiB</b> 로 같고,
/// 조각 수만 다르다 — 차이는 전부 재조립(복사 + 대여 왕래 + 조각당 디코드)의 값이다.
/// </para>
/// <list type="bullet">
///   <item><b>단일 프레임</b> — 조각 없음. 재조립기가 아예 만들어지지 않는 경로.</item>
///   <item><b>조각 4×1 KiB</b> — 메시지마다 4 KiB 대여-복사-반납 1회 왕래.</item>
///   <item><b>조각 16×256 B</b> — 조각 수에 대한 스케일 확인(복사 총량은 같다).</item>
/// </list>
/// <para>
/// <b>판정 기준.</b> 단일 프레임 팔의 메시지당 할당이 0 이면 "조건부 비용 0" 주장이 성립한다.
/// 조각 팔의 할당도 0 이어야 정상이다(대여 왕래는 공유 풀 TLS 를 왕복할 뿐이다) — 여기서
/// 0 이 아니면 재조립 경로 어딘가가 새고 있는 것이다. 시간 차이는 결함이 아니라 가격표다.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(BenchConfig))]
public class FragmentAssemblyBenchmarks
{
    private const int MessagesPerInvoke = 128;
    private const int LogicalMessageBytes = 4096;
    private const ushort BenchMessageId = 100;

    private Pipe _input = null!;
    private BenchConnection _connection = null!;
    private Task _readLoop = null!;
    private long _handled;

    private byte[] _singleFrameBlob = null!;
    private byte[] _fourFragmentBlob = null!;
    private byte[] _sixteenFragmentBlob = null!;

    [GlobalSetup]
    public void Setup()
    {
        FramingOptions framing = new() { MaxPayloadLength = LogicalMessageBytes };
        FixedHeaderFrameEncoder encoder = new(framing);

        _singleFrameBlob = EncodeMessages(encoder, fragmentCount: 1);
        _fourFragmentBlob = EncodeMessages(encoder, fragmentCount: 4);
        _sixteenFragmentBlob = EncodeMessages(encoder, fragmentCount: 16);

        // 벤치 하네스 파이프 — 블롭(≈70 KiB × 조각 오버헤드)이 한 번에 들어가야 하므로
        // 임계값을 넉넉히 둔다. 프로덕션 규약(유계, 9.6)의 검증 대상이 아니라 측정 장치다.
        _input = new Pipe(new PipeOptions(
            pauseWriterThreshold: 4 * 1024 * 1024,
            resumeWriterThreshold: 2 * 1024 * 1024,
            useSynchronizationContext: false));

        _connection = new BenchConnection(_input.Reader);

        MessageDispatcherBuilder builder = new();
        builder.MapRaw(new MessageId(BenchMessageId), context =>
        {
            // 읽기 루프 스레드에서 순차 실행된다 — 벤치 스레드는 Volatile 로 관측한다.
            Interlocked.Increment(ref _handled);
            return ValueTask.FromResult(DispatchStatus.Handled);
        });

        FramedConnectionHandler handler = new(
            new FixedHeaderFrameDecoder(framing), builder.Build());

        _readLoop = handler.RunAsync(_connection);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _input.Writer.Complete();
        _readLoop.GetAwaiter().GetResult();
        _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true, Description = "단일 프레임(4KiB, 조각 없음)", OperationsPerInvoke = MessagesPerInvoke)]
    public void SingleFrame() => PumpAndWait(_singleFrameBlob);

    [Benchmark(Description = "조각 4×1KiB(재조립)", OperationsPerInvoke = MessagesPerInvoke)]
    public void FourFragments() => PumpAndWait(_fourFragmentBlob);

    [Benchmark(Description = "조각 16×256B(재조립)", OperationsPerInvoke = MessagesPerInvoke)]
    public void SixteenFragments() => PumpAndWait(_sixteenFragmentBlob);

    /// <summary>블롭을 밀어 넣고 메시지 128개가 전부 디스패치될 때까지 기다린다.</summary>
    private void PumpAndWait(byte[] blob)
    {
        long target = Volatile.Read(ref _handled) + MessagesPerInvoke;

        // CA2012 억제: 파이프 임계값(4 MiB)이 블롭보다 훨씬 커서 이 쓰기는 동기 완료된다.
        // 설령 대기가 나와도 GetResult 는 완료까지 블로킹한다(파이프 구현의 성질).
#pragma warning disable CA2012
        _input.Writer.WriteAsync(blob).GetAwaiter().GetResult();
#pragma warning restore CA2012

        SpinWait spinner = default;
        while (Volatile.Read(ref _handled) < target)
        {
            spinner.SpinOnce();
        }
    }

    /// <summary>논리 메시지 4 KiB × 128개를 조각 수에 맞춰 와이어 형식으로 인코딩한다.</summary>
    private static byte[] EncodeMessages(FixedHeaderFrameEncoder encoder, int fragmentCount)
    {
        int fragmentBytes = LogicalMessageBytes / fragmentCount;
        byte[] payload = new byte[fragmentBytes];
        payload.AsSpan().Fill(0xCD);

        ArrayBufferWriter<byte> buffer = new();
        for (int message = 0; message < MessagesPerInvoke; message++)
        {
            for (int fragment = 0; fragment < fragmentCount; fragment++)
            {
                FrameFlags flags = FrameFlags.None;
                if (fragmentCount > 1)
                {
                    flags = fragment == fragmentCount - 1
                        ? FrameFlags.Fragmented | FrameFlags.EndOfMessage
                        : FrameFlags.Fragmented;
                }

                encoder.WriteHeader(
                    buffer,
                    new MessageEnvelope(new MessageId(BenchMessageId), flags, sequence: 0),
                    payload.Length);
                buffer.Write(payload);
            }
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>읽기 루프에 물릴 최소 커넥션 — 입력만 실제 파이프이고 출력은 쓰지 않는다.</summary>
    private sealed class BenchConnection(PipeReader input) : IConnection
    {
        private static readonly Pipe DummyOutput = new();

        public ConnectionId Id => new(1, 1);

        public PipeReader Input => input;

        public PipeWriter Output => DummyOutput.Writer;

        public IFeatureCollection Features { get; } = new FeatureCollection(capacity: 0);

        public CancellationToken ConnectionClosed => CancellationToken.None;

        public void Abort(in ConnectionCloseInfo info)
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}
