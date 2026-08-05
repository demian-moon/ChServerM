using System;
using System.Buffers;
using BenchmarkDotNet.Attributes;
using ChServerM.Serialization;
using ChServerM.Serialization.FlatBuffers;
using ChServerM.Serialization.MemoryPack;
using ChServerM.Serialization.Protobuf;

namespace ChServerM.Bench.Serialization;

/// <summary>
/// 4자 직렬화 벤치마크 — MemoryPack / Google.Protobuf / FlatSharp / MessagePack.
/// ADR-0002 의 미결 항목(페이로드 직렬화 기본값)을 확정하는 근거 수치를 만든다.
/// </summary>
/// <remarks>
/// <para>
/// 네 포맷 모두 <b>같은 필드 구성</b>(sender/text/timestamp)의 메시지를 다루고,
/// 어댑터가 있는 셋은 프로덕션과 같은 <see cref="IMessageSerializer{TMessage}"/> 경로로
/// 돈다. MessagePack 만 어댑터가 없어(ADR-0012) 동일 형태의 벤치 전용 래퍼를 쓰며,
/// 표준 리졸버(IL emit) 경로라 <b>JIT 전용 수치</b>다 — AOT 라면 소스 생성 리졸버로
/// 다시 재야 한다.
/// </para>
/// <para>
/// 직렬화 산출물 크기는 통계에 안 잡히므로 <see cref="Setup"/> 가 콘솔에 찍는다 —
/// 와이어 크기도 기본값 판단 재료다(BENCHMARKS.md 에 함께 기록).
/// </para>
/// </remarks>
[Config(typeof(BenchConfig))]
public class SerializationBenchmarks
{
    private readonly MemoryPackMessageSerializer<BenchMemoryPackMessage> _memoryPack = new();
    private readonly ProtobufMessageSerializer<BenchProtoMessage> _protobuf = new();
    private FlatSharpMessageSerializer<BenchFbMessage> _flatSharp = null!;
    private readonly MessagePackBenchSerializer<BenchMessagePackMessage> _messagePack = new();

    private BenchMemoryPackMessage _memoryPackMessage = null!;
    private BenchProtoMessage _protobufMessage = null!;
    private BenchFbMessage _flatSharpMessage = null!;
    private BenchMessagePackMessage _messagePackMessage = null!;

    private ReadOnlySequence<byte> _memoryPackEncoded;
    private ReadOnlySequence<byte> _protobufEncoded;
    private ReadOnlySequence<byte> _flatSharpEncoded;
    private ReadOnlySequence<byte> _messagePackEncoded;

    private ArrayBufferWriter<byte> _writer = null!;

    /// <summary>small: 채팅 한 줄 수준(~60B). large: 1KiB 본문 — 페이로드 지배 구간.</summary>
    [Params("small", "large")]
    public string Size { get; set; } = "small";

    [GlobalSetup]
    public void Setup()
    {
        const string Sender = "user_0123456789";
        string text = Size == "small" ? "프레이밍과 직렬화는 독립 축이다" : new string('가', 1024);
        const long Timestamp = 1_722_800_000_000;

        _flatSharp = new FlatSharpMessageSerializer<BenchFbMessage>(BenchFbMessage.Serializer);

        _memoryPackMessage = new BenchMemoryPackMessage { Sender = Sender, Text = text, Timestamp = Timestamp };
        _protobufMessage = new BenchProtoMessage { Sender = Sender, Text = text, Timestamp = Timestamp };
        _flatSharpMessage = new BenchFbMessage { Sender = Sender, Text = text, Timestamp = Timestamp };
        _messagePackMessage = new BenchMessagePackMessage { Sender = Sender, Text = text, Timestamp = Timestamp };

        _writer = new ArrayBufferWriter<byte>(16 * 1024);

        _memoryPackEncoded = Encode(_memoryPack, _memoryPackMessage);
        _protobufEncoded = Encode(_protobuf, _protobufMessage);
        _flatSharpEncoded = Encode(_flatSharp, _flatSharpMessage);
        _messagePackEncoded = Encode(_messagePack, _messagePackMessage);

        // 와이어 크기는 BDN 통계에 안 잡힌다. 로그로 남겨 BENCHMARKS.md 에 옮긴다.
        Console.WriteLine(
            $"[{Size}] wire bytes — MemoryPack: {_memoryPackEncoded.Length}, " +
            $"Protobuf: {_protobufEncoded.Length}, FlatSharp: {_flatSharpEncoded.Length}, " +
            $"MessagePack: {_messagePackEncoded.Length}");
    }

    private static ReadOnlySequence<byte> Encode<T>(IMessageSerializer<T> serializer, T message)
    {
        ArrayBufferWriter<byte> writer = new();
        serializer.Serialize(writer, message);
        return new ReadOnlySequence<byte>(writer.WrittenSpan.ToArray());
    }

    // ── 직렬화 ──────────────────────────────────────────────────────

    [Benchmark(Baseline = true, Description = "Serialize MemoryPack")]
    public int SerializeMemoryPack()
    {
        _writer.ResetWrittenCount();
        _memoryPack.Serialize(_writer, _memoryPackMessage);
        return _writer.WrittenCount;
    }

    [Benchmark(Description = "Serialize Protobuf")]
    public int SerializeProtobuf()
    {
        _writer.ResetWrittenCount();
        _protobuf.Serialize(_writer, _protobufMessage);
        return _writer.WrittenCount;
    }

    [Benchmark(Description = "Serialize FlatSharp")]
    public int SerializeFlatSharp()
    {
        _writer.ResetWrittenCount();
        _flatSharp.Serialize(_writer, _flatSharpMessage);
        return _writer.WrittenCount;
    }

    [Benchmark(Description = "Serialize MessagePack")]
    public int SerializeMessagePack()
    {
        _writer.ResetWrittenCount();
        _messagePack.Serialize(_writer, _messagePackMessage);
        return _writer.WrittenCount;
    }

    // ── 역직렬화 ────────────────────────────────────────────────────

    [Benchmark(Description = "Deserialize MemoryPack")]
    public BenchMemoryPackMessage? DeserializeMemoryPack()
        => _memoryPack.TryDeserialize(_memoryPackEncoded, out BenchMemoryPackMessage message) ? message : null;

    [Benchmark(Description = "Deserialize Protobuf")]
    public BenchProtoMessage? DeserializeProtobuf()
        => _protobuf.TryDeserialize(_protobufEncoded, out BenchProtoMessage message) ? message : null;

    [Benchmark(Description = "Deserialize FlatSharp")]
    public BenchFbMessage? DeserializeFlatSharp()
        => _flatSharp.TryDeserialize(_flatSharpEncoded, out BenchFbMessage message) ? message : null;

    [Benchmark(Description = "Deserialize MessagePack")]
    public BenchMessagePackMessage? DeserializeMessagePack()
        => _messagePack.TryDeserialize(_messagePackEncoded, out BenchMessagePackMessage message) ? message : null;

    /// <summary>
    /// MessagePack 벤치 전용 래퍼. 어댑터 어셈블리를 만들지 않는 이유는 ADR-0012 —
    /// 비교군에는 어댑터와 같은 형태(계약 경유 호출)면 충분하다.
    /// </summary>
    private sealed class MessagePackBenchSerializer<T> : IMessageSerializer<T>
    {
        public void Serialize(IBufferWriter<byte> writer, in T message)
            => global::MessagePack.MessagePackSerializer.Serialize(writer, message);

        public bool TryDeserialize(in ReadOnlySequence<byte> payload, out T message)
        {
            message = global::MessagePack.MessagePackSerializer.Deserialize<T>(payload)!;
            return true;
        }
    }
}
