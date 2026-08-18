using System;
using System.Buffers;
using ChServerM.Serialization;
using Xunit;

namespace ChServerM.Serialization.FlatBuffers.Tests;

/// <summary>
/// <see cref="FlatSharpMessageSerializerProvider"/> 의 명시 등록 계약 검증.
/// </summary>
public sealed class FlatSharpMessageSerializerProviderTests
{
    [Fact]
    public void Find_RegisteredType_ReturnsWorkingSerializer()
    {
        FlatSharpMessageSerializerProvider provider = new();
        provider.Register(FbChatMessage.Serializer);

        IMessageSerializer<FbChatMessage>? serializer = provider.Find<FbChatMessage>();
        Assert.NotNull(serializer);

        ArrayBufferWriter<byte> writer = new();
        serializer.Serialize(writer, new FbChatMessage { Text = "등록 경로" });
        Assert.True(serializer.TryDeserialize(new ReadOnlySequence<byte>(writer.WrittenMemory), out FbChatMessage? decoded));
        Assert.NotNull(decoded);
        Assert.Equal("등록 경로", decoded.Text);
    }

    [Fact]
    public void Find_UnregisteredType_ReturnsNull()
    {
        FlatSharpMessageSerializerProvider provider = new();
        provider.Register(FbChatMessage.Serializer);

        Assert.Null(provider.Find<FbChatMessageV2>());
    }

    [Fact]
    public void Find_ReturnsSameInstance()
    {
        FlatSharpMessageSerializerProvider provider = new();
        provider.Register(FbChatMessage.Serializer);

        Assert.Same(provider.Find<FbChatMessage>(), provider.Find<FbChatMessage>());
    }

    [Fact]
    public void Register_Duplicate_Throws()
    {
        FlatSharpMessageSerializerProvider provider = new();
        provider.Register(FbChatMessage.Serializer);

        Assert.Throws<ArgumentException>(() => provider.Register(FbChatMessage.Serializer));
    }
}
