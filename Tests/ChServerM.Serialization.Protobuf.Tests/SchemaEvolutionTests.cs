using System.Buffers;
using Xunit;

namespace ChServerM.Serialization.Protobuf.Tests;

/// <summary>
/// proto3 스키마 진화 — 필드 추가가 양방향 호환임을 고정한다.
/// </summary>
/// <remarks>
/// ROADMAP Phase 6 "스키마 진화 테스트" 항목의 protobuf 쪽 절반이다.
/// 같은 필드 번호 = 같은 의미라는 규약 위에서, V2 가 추가한 필드를
/// V1 리더는 모르는 필드로 보존하고, V1 데이터를 V2 리더는 기본값으로 채운다.
/// 이 성질이 protobuf 를 "구버전 클라이언트 공존" 요구의 기본 후보로 만든다.
/// </remarks>
public sealed class SchemaEvolutionTests
{
    private static readonly ProtobufMessageSerializer<ProtoChatMessage> V1Serializer = new();
    private static readonly ProtobufMessageSerializer<ProtoChatMessageV2> V2Serializer = new();

    [Fact]
    public void NewData_OldReader_IgnoresAddedField()
    {
        ProtoChatMessageV2 v2 = new()
        {
            Sender = "심연",
            Text = "본문",
            Timestamp = 42,
            Priority = 7,
        };

        ArrayBufferWriter<byte> writer = new();
        V2Serializer.Serialize(writer, v2);

        Assert.True(V1Serializer.TryDeserialize(new ReadOnlySequence<byte>(writer.WrittenMemory), out ProtoChatMessage v1));
        Assert.Equal(v2.Sender, v1.Sender);
        Assert.Equal(v2.Text, v1.Text);
        Assert.Equal(v2.Timestamp, v1.Timestamp);
    }

    [Fact]
    public void OldData_NewReader_FillsDefault()
    {
        ProtoChatMessage v1 = new() { Sender = "심연", Text = "본문", Timestamp = 42 };

        ArrayBufferWriter<byte> writer = new();
        V1Serializer.Serialize(writer, v1);

        Assert.True(V2Serializer.TryDeserialize(new ReadOnlySequence<byte>(writer.WrittenMemory), out ProtoChatMessageV2 v2));
        Assert.Equal(v1.Text, v2.Text);
        Assert.Equal(0, v2.Priority);
    }

    [Fact]
    public void NewData_OldReader_RoundtripsBack_PreservingUnknownField()
    {
        // 중계 서버 시나리오 — V1 리더가 읽고 다시 쓴 바이트에 V2 필드가 살아남아야
        // "모르는 필드를 버리는 중간자" 문제가 없다.
        ProtoChatMessageV2 v2 = new() { Sender = "s", Text = "t", Timestamp = 1, Priority = 9 };

        ArrayBufferWriter<byte> firstPass = new();
        V2Serializer.Serialize(firstPass, v2);
        Assert.True(V1Serializer.TryDeserialize(new ReadOnlySequence<byte>(firstPass.WrittenMemory), out ProtoChatMessage v1));

        ArrayBufferWriter<byte> secondPass = new();
        V1Serializer.Serialize(secondPass, v1);
        Assert.True(V2Serializer.TryDeserialize(new ReadOnlySequence<byte>(secondPass.WrittenMemory), out ProtoChatMessageV2 restored));

        Assert.Equal(9, restored.Priority);
    }
}
