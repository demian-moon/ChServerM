using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Hosting.Dispatch;
using ChServerM.Identity;
using ChServerM.Time;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 커넥션별 속도 제한기의 단위 계약 — 토큰 버킷의 버스트·리필·상한과, 상태가
/// 커넥션 피처에 살아 커넥션별로 독립임을 가짜 시계로 결정적으로 고정한다.
/// </summary>
public sealed class RateLimiterTests
{
    private static MessageContext ContextWith(IConnection connection)
    {
        MessageContext context = new(connection);
        context.BeginFrame(
            new MessageEnvelope(new MessageId(1), FrameFlags.None, 0),
            default, MonotonicTimestamp.None, CancellationToken.None);
        return context;
    }

    [Fact]
    public void Bursts_then_rejects_when_bucket_empty()
    {
        ManualTimeProvider time = new();
        PerConnectionRateLimiter limiter = new(
            new PerConnectionRateLimitOptions { PermitsPerSecond = 100, BurstCapacity = 3 }, time);
        MessageContext context = ContextWith(new StubConnection());

        Assert.True(limiter.TryAcquire(context));
        Assert.True(limiter.TryAcquire(context));
        Assert.True(limiter.TryAcquire(context));
        Assert.False(limiter.TryAcquire(context)); // 버스트 3개 소진
    }

    [Fact]
    public void Refills_over_time()
    {
        ManualTimeProvider time = new();
        PerConnectionRateLimiter limiter = new(
            new PerConnectionRateLimitOptions { PermitsPerSecond = 100, BurstCapacity = 1 }, time);
        MessageContext context = ContextWith(new StubConnection());

        Assert.True(limiter.TryAcquire(context));
        Assert.False(limiter.TryAcquire(context));

        // 0.01초 = 100/s × 0.01 = 1 토큰 충전.
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.True(limiter.TryAcquire(context));
        Assert.False(limiter.TryAcquire(context));
    }

    [Fact]
    public void Buckets_are_independent_per_connection()
    {
        // 상태가 Connection.Features 에 사니, 한 커넥션 소진이 다른 커넥션에 영향 없다.
        ManualTimeProvider time = new();
        PerConnectionRateLimiter limiter = new(
            new PerConnectionRateLimitOptions { PermitsPerSecond = 10, BurstCapacity = 1 }, time);

        MessageContext a = ContextWith(new StubConnection());
        MessageContext b = ContextWith(new StubConnection());

        Assert.True(limiter.TryAcquire(a));
        Assert.False(limiter.TryAcquire(a)); // a 소진

        // b 는 자기 버킷이 가득하다.
        Assert.True(limiter.TryAcquire(b));
    }

    [Fact]
    public void Rejects_invalid_options()
    {
        Assert.Throws<InvalidOperationException>(
            static () => new PerConnectionRateLimiter(new PerConnectionRateLimitOptions { PermitsPerSecond = 0 }));
        Assert.Throws<InvalidOperationException>(
            static () => new PerConnectionRateLimiter(new PerConnectionRateLimitOptions { BurstCapacity = 0 }));
    }

    /// <summary>피처만 필요한 무동작 커넥션 — 속도 제한 상태 저장소 역할.</summary>
    private sealed class StubConnection : IConnection
    {
        public ConnectionId Id => new(1, 0);

        public System.IO.Pipelines.PipeReader Input => throw new NotSupportedException();

        public System.IO.Pipelines.PipeWriter Output => throw new NotSupportedException();

        public IFeatureCollection Features { get; } = new FeatureCollection(capacity: 1);

        public CancellationToken ConnectionClosed => CancellationToken.None;

        public void Abort(in ConnectionCloseInfo info)
        {
        }

        public ValueTask DisposeAsync() => default;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _timestamp, delta.Ticks);
    }
}
