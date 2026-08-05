using System.Buffers;
using Xunit;

namespace ChServerM.Serialization.FlatBuffers.Tests;

/// <summary>
/// FlatBuffers 스키마 진화 — 테이블 끝에 필드를 추가하면 양방향 호환임을 고정한다.
/// </summary>
/// <remarks>
/// ROADMAP Phase 6 "스키마 진화 테스트" 항목의 FlatBuffers 쪽이다. vtable 기반이라
/// 없는 필드 접근은 기본값으로 풀린다. 단 protobuf 와 달리 <b>모르는 필드의 보존은
/// 없다</b> — V1 리더가 다시 쓰면 V2 필드는 사라진다. 중계 시나리오에서는 protobuf 와
/// 성질이 다르다는 것까지가 이 테스트가 문서화하는 내용이다.
/// </remarks>
public sealed class SchemaEvolutionTests
{
    private static readonly FlatSharpMessageSerializer<FbChatMessage> V1Serializer =
        new(FbChatMessage.Serializer);

    private static readonly FlatSharpMessageSerializer<FbChatMessageV2> V2Serializer =
        new(FbChatMessageV2.Serializer);

    [Fact]
    public void NewData_OldReader_IgnoresAddedField()
    {
        FbChatMessageV2 v2 = new() { Sender = "심연", Text = "본문", Timestamp = 42, Priority = 7 };

        ArrayBufferWriter<byte> writer = new();
        V2Serializer.Serialize(writer, v2);

        Assert.True(V1Serializer.TryDeserialize(new ReadOnlySequence<byte>(writer.WrittenMemory), out FbChatMessage v1));
        Assert.Equal(v2.Sender, v1.Sender);
        Assert.Equal(v2.Text, v1.Text);
        Assert.Equal(v2.Timestamp, v1.Timestamp);
    }

    [Fact]
    public void OldData_NewReader_FillsDefault()
    {
        FbChatMessage v1 = new() { Sender = "심연", Text = "본문", Timestamp = 42 };

        ArrayBufferWriter<byte> writer = new();
        V1Serializer.Serialize(writer, v1);

        Assert.True(V2Serializer.TryDeserialize(new ReadOnlySequence<byte>(writer.WrittenMemory), out FbChatMessageV2 v2));
        Assert.Equal(v1.Text, v2.Text);
        Assert.Equal(0, v2.Priority);
    }
}
