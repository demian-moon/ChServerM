using MemoryPack;

namespace ChServerM.Serialization.MemoryPack.Tests;

/// <summary>참조 타입 메시지의 대표. 문자열·정수 혼합 필드로 왕복을 검증한다.</summary>
[MemoryPackable]
internal sealed partial class ChatMessage
{
    public string Sender { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public long Timestamp { get; set; }
}

/// <summary>값 타입 메시지의 대표. 핫패스 메시지는 struct 가 기본이다.</summary>
[MemoryPackable]
internal readonly partial record struct MoveCommand(float X, float Y, uint Tick);

/// <summary>
/// 제공자 테스트 전용. <b>다른 테스트가 먼저 만지면 안 된다</b> —
/// "한 번도 직렬화한 적 없는 타입도 Find 가 찾는다"(거짓 음성 없음)를 고정하는 타입이다.
/// </summary>
[MemoryPackable]
internal sealed partial class ColdMessage
{
    public int Value { get; set; }
}

/// <summary>
/// MemoryPack 이 모르는 타입. 제공자가 null 을 돌려줘야 한다.
/// 제네릭 인자로만 쓰므로 인스턴스화가 없어도 되는 record struct 다(CA1812 회피).
/// </summary>
internal readonly record struct NotPackableMessage(int Value);

/// <summary>스키마 진화 테스트용 — <see cref="ChatMessage"/> 에 필드 하나를 더한 기본 모드 타입.</summary>
[MemoryPackable]
internal sealed partial class ChatMessageWide
{
    public string Sender { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public long Timestamp { get; set; }

    public int Priority { get; set; }
}

/// <summary>스키마 진화 테스트용 — 버전 관용 모드 V1.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
internal sealed partial class TolerantMessage
{
    [MemoryPackOrder(0)]
    public string Text { get; set; } = string.Empty;
}

/// <summary>스키마 진화 테스트용 — 버전 관용 모드 V2 (끝에 필드 추가).</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
internal sealed partial class TolerantMessageV2
{
    [MemoryPackOrder(0)]
    public string Text { get; set; } = string.Empty;

    [MemoryPackOrder(1)]
    public int Priority { get; set; }
}
