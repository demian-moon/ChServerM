using System;
using System.Buffers;
using ChServerM.Serialization;
using Xunit;

namespace ChServerM.Serialization.Protobuf.Tests;

/// <summary>
/// <see cref="ProtobufMessageSerializerProvider"/> 의 명시 등록 계약 검증.
/// </summary>
public sealed class ProtobufMessageSerializerProviderTests
{
    [Fact]
    public void Find_RegisteredType_ReturnsWorkingSerializer()
    {
        ProtobufMessageSerializerProvider provider = new();
        provider.Register<ProtoChatMessage>();

        IMessageSerializer<ProtoChatMessage>? serializer = provider.Find<ProtoChatMessage>();
        Assert.NotNull(serializer);

        ArrayBufferWriter<byte> writer = new();
        serializer.Serialize(writer, new ProtoChatMessage { Text = "등록 경로" });
        Assert.True(serializer.TryDeserialize(new ReadOnlySequence<byte>(writer.WrittenMemory), out ProtoChatMessage decoded));
        Assert.Equal("등록 경로", decoded.Text);
    }

    [Fact]
    public void Find_UnregisteredType_ReturnsNull()
    {
        ProtobufMessageSerializerProvider provider = new();
        provider.Register<ProtoChatMessage>();

        Assert.Null(provider.Find<ProtoChatMessageV2>());
    }

    [Fact]
    public void Find_ReturnsSameInstance()
    {
        ProtobufMessageSerializerProvider provider = new();
        provider.Register<ProtoChatMessage>();

        Assert.Same(provider.Find<ProtoChatMessage>(), provider.Find<ProtoChatMessage>());
    }

    [Fact]
    public void Register_Duplicate_Throws()
    {
        // 중복 등록은 조립 실수다. 덮어쓰면 어느 직렬화기가 도는지 알 수 없게 된다.
        ProtobufMessageSerializerProvider provider = new();
        provider.Register<ProtoChatMessage>();

        Assert.Throws<ArgumentException>(() => provider.Register<ProtoChatMessage>());
    }
}
