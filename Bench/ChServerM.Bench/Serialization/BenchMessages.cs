using MemoryPack;
using MessagePack;

namespace ChServerM.Bench.Serialization;

/// <summary>MemoryPack 쪽 벤치 메시지. 필드 구성은 네 포맷이 동일하다.</summary>
[MemoryPackable]
public sealed partial class BenchMemoryPackMessage
{
    public string Sender { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public long Timestamp { get; set; }
}

/// <summary>MessagePack 쪽 벤치 메시지. 필드 구성은 네 포맷이 동일하다.</summary>
[MessagePackObject]
public sealed class BenchMessagePackMessage
{
    [Key(0)]
    public string Sender { get; set; } = string.Empty;

    [Key(1)]
    public string Text { get; set; } = string.Empty;

    [Key(2)]
    public long Timestamp { get; set; }
}
