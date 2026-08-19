using System;
using System.Globalization;
using System.Text;
using ChServerM.Content;
using ChServerM.Diagnostics;
using ChServerM.Handshake;
using ChServerM.Identity;
using ChServerM.Time;
using Xunit;
using EventId = ChServerM.Diagnostics.EventId;

namespace ChServerM.Core.Tests;

/// <summary>
/// 감사 2026-08-18 C-4 — 진단용 값 타입의 <see cref="ISpanFormattable"/>/<see cref="IUtf8SpanFormattable"/>
/// 구현이 지켜야 할 계약을 전 타입에 일괄 검증한다.
/// <list type="number">
///   <item><description><c>TryFormat(char)</c> 출력 == <c>ToString()</c> — 진단 표기 변경 금지</description></item>
///   <item><description><c>TryFormat(byte)</c> 출력 == <c>ToString()</c> 의 UTF-8 인코딩</description></item>
///   <item><description>버퍼가 1 짧으면 <see langword="false"/> + written 0 — 부분 출력을 남기지 않는다</description></item>
///   <item><description>보간 문자열 경유(<c>$"{value}"</c>)도 기존 표기와 동일 — ZLogger·보간 핸들러가
///   실제로 타는 경로가 이것이다</description></item>
/// </list>
/// </summary>
public sealed class SpanFormattableContractTests
{
    /// <summary>한 값에 대해 위 계약 전부를 검증한다.</summary>
    private static void AssertFormatContract<T>(T value)
        where T : struct, ISpanFormattable, IUtf8SpanFormattable
    {
        string expected = value.ToString()!;
        byte[] expectedUtf8 = Encoding.UTF8.GetBytes(expected);

        // (1) TryFormat(char) — 정확히 맞는 버퍼에서 성공하고 출력이 ToString 과 같다.
        Span<char> chars = new char[expected.Length];
        Assert.True(value.TryFormat(chars, out int charsWritten, default, null));
        Assert.Equal(expected.Length, charsWritten);
        Assert.Equal(expected, new string(chars));

        // format/provider 는 무시된다 — 엉뚱한 형식 지정자를 줘도 표기는 같다.
        // (InvariantGlobalization=true 라 특정 문화권은 못 만들지만, 무시 계약 검증에는 충분하다.)
        chars.Clear();
        Assert.True(value.TryFormat(chars, out charsWritten, "X8", CultureInfo.CurrentCulture));
        Assert.Equal(expected, new string(chars[..charsWritten]));

        // (2) TryFormat(byte) — 출력이 ToString 의 UTF-8 인코딩과 바이트 단위로 같다.
        Span<byte> bytes = new byte[expectedUtf8.Length];
        Assert.True(value.TryFormat(bytes, out int bytesWritten, default, null));
        Assert.Equal(expectedUtf8.Length, bytesWritten);
        Assert.True(bytes.SequenceEqual(expectedUtf8));

        // (3) 1 짧은 버퍼 — 실패 + written 0 (부분 출력이 "성공한 척" 새 나가면 안 된다).
        Assert.False(value.TryFormat(new char[expected.Length - 1], out charsWritten, default, null));
        Assert.Equal(0, charsWritten);
        Assert.False(value.TryFormat(new byte[expectedUtf8.Length - 1], out bytesWritten, default, null));
        Assert.Equal(0, bytesWritten);

        // (4) 보간 핸들러 경유 — DefaultInterpolatedStringHandler 가 ISpanFormattable 을 탄다.
        Assert.Equal(expected, string.Create(CultureInfo.InvariantCulture, $"{value}"));

        // ISpanFormattable 의 ToString(format, provider) 은 기존 ToString 에 위임한다.
        Assert.Equal(expected, value.ToString(null, CultureInfo.InvariantCulture));
        Assert.Equal(expected, value.ToString("X8", CultureInfo.CurrentCulture));
    }

    [Fact]
    public void ConnectionId_FormatContract()
    {
        AssertFormatContract(ConnectionId.None);            // "conn:none"
        AssertFormatContract(new ConnectionId(0, 1));
        AssertFormatContract(new ConnectionId(7, 3));
        AssertFormatContract(new ConnectionId(uint.MaxValue, uint.MaxValue));
    }

    [Fact]
    public void SessionId_FormatContract()
    {
        AssertFormatContract(SessionId.None);               // "sess:0"
        AssertFormatContract(new SessionId(new ObjectId(123_456_789L)));
        AssertFormatContract(new SessionId(new ObjectId(long.MaxValue)));
    }

    [Fact]
    public void JobId_FormatContract()
    {
        AssertFormatContract(JobId.None);                   // "job:0/0"
        AssertFormatContract(new JobId(1, 2));
        AssertFormatContract(new JobId(ulong.MaxValue, ulong.MaxValue));
    }

    [Fact]
    public void NodeId_FormatContract()
    {
        AssertFormatContract(NodeId.None);                  // "node:0" — 센티넬도 표기는 그대로
        AssertFormatContract(new NodeId(1));
        AssertFormatContract(new NodeId(ObjectId.MaxNodeId));
    }

    [Fact]
    public void ObjectId_FormatContract()
    {
        AssertFormatContract(ObjectId.None);                // "oid:0"
        AssertFormatContract(new ObjectId(long.MaxValue));
        AssertFormatContract(new ObjectId(-1));             // 원본 수치 생성자는 음수도 허용한다
        AssertFormatContract(ObjectId.Create(1_234_567L, 42, 99));
    }

    [Fact]
    public void MessageId_FormatContract()
    {
        AssertFormatContract(MessageId.None);               // "msg:0"
        AssertFormatContract(FrameworkMessageIds.Heartbeat);
        AssertFormatContract(new MessageId(ushort.MaxValue));
    }

    [Fact]
    public void PartitionKey_FormatContract()
    {
        AssertFormatContract(PartitionKey.FromPrecomputedHash(0));                // "pk:0000000000000000"
        AssertFormatContract(PartitionKey.FromPrecomputedHash(ulong.MaxValue));   // "pk:ffffffffffffffff"
        AssertFormatContract(PartitionKey.FromValue(1));
        AssertFormatContract(PartitionKey.FromValue(0xDEAD_BEEF_CAFE_F00DUL));
    }

    [Fact]
    public void MonotonicTimestamp_FormatContract()
    {
        AssertFormatContract(MonotonicTimestamp.None);      // "mono:0"
        AssertFormatContract(MonotonicTimestamp.FromRaw(long.MaxValue));
        AssertFormatContract(MonotonicTimestamp.FromRaw(-42)); // 음수 raw 도 표기 그대로
    }

    [Fact]
    public void EventId_FormatContract()
    {
        AssertFormatContract(new EventId(1000));            // 이름 없음 → 번호
        AssertFormatContract(new EventId(-5));              // 음수 번호
        AssertFormatContract(new EventId(7, "connection.accepted"));
        AssertFormatContract(new EventId(8, "연결.수락"));   // 비 ASCII 이름 — UTF-8 길이가 문자 수와 다르다
    }

    [Fact]
    public void ContentFingerprint_FormatContract()
    {
        AssertFormatContract(ContentFingerprint.None);      // "(none)"
        AssertFormatContract(new ContentFingerprint(1, 2)); // 선행 0 이 채워진 16진 32자리
        AssertFormatContract(new ContentFingerprint(ulong.MaxValue, ulong.MaxValue));
        AssertFormatContract(new ContentFingerprint(0, 1)); // High 가 0 이어도 32자리를 유지한다
    }

    [Fact]
    public void ProtocolVersionRange_FormatContract()
    {
        AssertFormatContract(default(ProtocolVersionRange)); // "v[0,0]" — 센티넬도 표기는 그대로
        AssertFormatContract(new ProtocolVersionRange(1, 1));
        AssertFormatContract(new ProtocolVersionRange(1, ushort.MaxValue));
    }
}
