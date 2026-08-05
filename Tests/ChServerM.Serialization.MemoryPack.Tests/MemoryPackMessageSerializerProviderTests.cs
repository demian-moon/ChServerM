using System.Buffers;
using ChServerM.Serialization;
using Xunit;

namespace ChServerM.Serialization.MemoryPack.Tests;

/// <summary>
/// <see cref="MemoryPackMessageSerializerProvider"/> 의 판정 계약 검증.
/// </summary>
public sealed class MemoryPackMessageSerializerProviderTests
{
    private static readonly MemoryPackMessageSerializerProvider Provider =
        MemoryPackMessageSerializerProvider.Instance;

    [Fact]
    public void Find_ColdPackableType_ReturnsSerializer()
    {
        // ColdMessage 는 이 테스트에서 처음 만지는 타입이다. 정적 생성자가 아직 돌지
        // 않은 타입을 "미등록"으로 오판하면(거짓 음성) 조립이 이유 없이 실패한다 —
        // 그 회귀를 여기서 고정한다.
        IMessageSerializer<ColdMessage>? serializer = Provider.Find<ColdMessage>();

        Assert.NotNull(serializer);

        ArrayBufferWriter<byte> writer = new();
        serializer.Serialize(writer, new ColdMessage { Value = 42 });
        Assert.True(serializer.TryDeserialize(new ReadOnlySequence<byte>(writer.WrittenMemory), out ColdMessage decoded));
        Assert.Equal(42, decoded.Value);
    }

    [Fact]
    public void Find_BuiltinString_ReturnsSerializer()
    {
        // 문자열은 MemoryPack 기본 지원 타입이다 — [MemoryPackable] 없이도 찾아야 한다.
        Assert.NotNull(Provider.Find<string>());
    }

    [Fact]
    public void Find_UnregisteredType_ReturnsNull()
    {
        Assert.Null(Provider.Find<NotPackableMessage>());
    }

    [Fact]
    public void Find_ReturnsSameInstance()
    {
        // 계약상 조회는 조립 시점 1회지만, 상태 없는 직렬화기를 매번 새로 만들 이유도 없다.
        Assert.Same(Provider.Find<ChatMessage>(), Provider.Find<ChatMessage>());
    }
}
