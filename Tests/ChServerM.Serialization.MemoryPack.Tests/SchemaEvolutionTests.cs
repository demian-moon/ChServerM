using System.Buffers;
using Xunit;

namespace ChServerM.Serialization.MemoryPack.Tests;

/// <summary>
/// MemoryPack 스키마 진화 — 기본 모드의 호환성이 <b>단방향</b>임을 테스트로 문서화한다.
/// </summary>
/// <remarks>
/// ROADMAP Phase 6 "스키마 진화 테스트" 항목의 MemoryPack 쪽이다. 기본(Object) 모드는
/// 멤버 수를 헤더에 적으므로 <b>구버전 데이터 → 신버전 리더</b>는 부족분을 기본값으로
/// 채워 성공하지만(실측으로 확인된 동작이다 — 처음 가정은 양방향 비호환이었다),
/// <b>신버전 데이터 → 구버전 리더</b>는 초과 멤버를 건너뛸 길이 정보가 없어 실패한다.
/// 롤링 배포에서는 구서버가 신클라 데이터를 읽는 후자가 반드시 발생하므로, 그 경계에는
/// <c>GenerateType.VersionTolerant</c> 를 명시해야 한다. 이 성질 차이가 기본값 ADR 의
/// 판단 재료다.
/// </remarks>
public sealed class SchemaEvolutionTests
{
    [Fact]
    public void DefaultMode_OldData_NewReader_FillsDefault()
    {
        // 허용되는 방향 — 멤버 수 3 < 4 는 부족분을 기본값으로 채운다.
        MemoryPackMessageSerializer<ChatMessage> v1 = new();
        MemoryPackMessageSerializer<ChatMessageWide> v2 = new();

        ArrayBufferWriter<byte> writer = new();
        v1.Serialize(writer, new ChatMessage { Sender = "심연", Text = "본문", Timestamp = 42 });

        Assert.True(v2.TryDeserialize(new ReadOnlySequence<byte>(writer.WrittenMemory), out ChatMessageWide decoded));
        Assert.Equal("본문", decoded.Text);
        Assert.Equal(0, decoded.Priority);
    }

    [Fact]
    public void DefaultMode_NewData_OldReader_Fails()
    {
        // 비호환 방향 — 초과 멤버를 건너뛸 길이 정보가 기본 모드에는 없다.
        MemoryPackMessageSerializer<ChatMessage> v1 = new();
        MemoryPackMessageSerializer<ChatMessageWide> v2 = new();

        ArrayBufferWriter<byte> writer = new();
        v2.Serialize(writer, new ChatMessageWide { Sender = "심연", Text = "본문", Timestamp = 42, Priority = 7 });

        Assert.False(v1.TryDeserialize(new ReadOnlySequence<byte>(writer.WrittenMemory), out _));
    }

    [Fact]
    public void VersionTolerantMode_OldData_NewReader_FillsDefault()
    {
        MemoryPackMessageSerializer<TolerantMessage> v1 = new();
        MemoryPackMessageSerializer<TolerantMessageV2> v2 = new();

        ArrayBufferWriter<byte> writer = new();
        v1.Serialize(writer, new TolerantMessage { Text = "본문" });

        Assert.True(v2.TryDeserialize(new ReadOnlySequence<byte>(writer.WrittenMemory), out TolerantMessageV2 decoded));
        Assert.Equal("본문", decoded.Text);
        Assert.Equal(0, decoded.Priority);
    }

    [Fact]
    public void VersionTolerantMode_NewData_OldReader_IgnoresAddedField()
    {
        MemoryPackMessageSerializer<TolerantMessage> v1 = new();
        MemoryPackMessageSerializer<TolerantMessageV2> v2 = new();

        ArrayBufferWriter<byte> writer = new();
        v2.Serialize(writer, new TolerantMessageV2 { Text = "본문", Priority = 7 });

        Assert.True(v1.TryDeserialize(new ReadOnlySequence<byte>(writer.WrittenMemory), out TolerantMessage decoded));
        Assert.Equal("본문", decoded.Text);
    }
}
