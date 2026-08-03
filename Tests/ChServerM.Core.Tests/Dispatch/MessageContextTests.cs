using System;
using System.Buffers;
using System.Threading;
using ChServerM.Core.Tests.Connections;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Identity;
using ChServerM.Time;
using Xunit;

namespace ChServerM.Core.Tests.Dispatch;

/// <summary>
/// 레거시는 "페이로드는 핸들러 반환 전까지만 유효" 라는 계약을 주석으로만 적어두고
/// <c>ToArray()</c> 로 위반했다. 여기서는 <see cref="MessageContext.EndFrame"/> 가
/// 참조를 실제로 끊는지 검증한다.
/// </summary>
public sealed class MessageContextTests
{
    private interface IScopedFeature
    {
    }

    private sealed class ScopedFeature : IScopedFeature
    {
    }

    private static ReadOnlySequence<byte> Payload(int length) => new(new byte[length]);

    [Fact]
    public void Constructor_NullConnection_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MessageContext(null!));
    }

    [Fact]
    public void BeginFrame_ExposesFrameData()
    {
        using StubConnection connection = new();
        MessageContext context = new(connection);
        FrameHeader header = new(new MessageId(9), 32);
        MonotonicTimestamp now = MonotonicTimestamp.FromRaw(12345);
        using CancellationTokenSource cts = new();

        context.BeginFrame(header, Payload(32), now, cts.Token);

        Assert.Equal(header, context.Header);
        Assert.Equal(32, context.Payload.Length);
        Assert.Equal(now, context.ReceivedAt);
        Assert.Equal(cts.Token, context.CancellationToken);
        Assert.Same(connection, context.Connection);
    }

    [Fact]
    public void EndFrame_ReleasesPayloadReference()
    {
        // 이 한 줄이 사용 후 해제를 막는다. 참조가 남으면 이미 반납된 버퍼를 가리킨다.
        using StubConnection connection = new();
        MessageContext context = new(connection);
        context.BeginFrame(new FrameHeader(new MessageId(1), 64), Payload(64), MonotonicTimestamp.FromRaw(1), default);

        context.EndFrame();

        Assert.Equal(0, context.Payload.Length);
        Assert.Equal(default(FrameHeader), context.Header);
        Assert.True(context.ReceivedAt.IsNone);
    }

    [Fact]
    public void EndFrame_ClearsPerMessageFeatures()
    {
        // 메시지 스코프 기능이 다음 프레임으로 새면 인증 결과가 잘못 재사용된다.
        using StubConnection connection = new();
        MessageContext context = new(connection);
        context.BeginFrame(new FrameHeader(new MessageId(1), 0), default, MonotonicTimestamp.FromRaw(1), default);
        context.Features.Set<IScopedFeature>(new ScopedFeature());

        context.EndFrame();

        Assert.Null(context.Features.Get<IScopedFeature>());
    }

    [Fact]
    public void EndFrame_DoesNotTouchConnectionFeatures()
    {
        // 커넥션 스코프 기능(TLS 정보 등)은 프레임 경계를 넘어 살아남아야 한다.
        using StubConnection connection = new();
        connection.Features.Set<IScopedFeature>(new ScopedFeature());
        MessageContext context = new(connection);
        context.BeginFrame(new FrameHeader(new MessageId(1), 0), default, MonotonicTimestamp.FromRaw(1), default);

        context.EndFrame();

        Assert.NotNull(connection.Features.Get<IScopedFeature>());
    }

    [Fact]
    public void Reuse_AcrossFrames_ShowsOnlyCurrentFrame()
    {
        // 커넥션당 하나를 재사용한다 — 메시지당 할당 0 의 근거.
        using StubConnection connection = new();
        MessageContext context = new(connection);

        context.BeginFrame(new FrameHeader(new MessageId(1), 16), Payload(16), MonotonicTimestamp.FromRaw(1), default);
        context.EndFrame();
        context.BeginFrame(new FrameHeader(new MessageId(2), 32), Payload(32), MonotonicTimestamp.FromRaw(2), default);

        Assert.Equal(new MessageId(2), context.Header.MessageId);
        Assert.Equal(32, context.Payload.Length);
    }
}
