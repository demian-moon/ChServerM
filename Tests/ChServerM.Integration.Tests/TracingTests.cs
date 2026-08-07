using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Hosting.Dispatch;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>추적 테스트 컬렉션 — 전역 <see cref="ActivityListener"/> 를 다루는 클래스를 묶는다.</summary>
/// <remarks>
/// <see cref="ActivityListener"/> 는 <b>프로세스 전역</b>이다. 리스너를 붙이는 두 클래스
/// (<see cref="TracingTests"/>·<see cref="TracingEndToEndTests"/>)가 xUnit 기본 병렬로 동시에
/// 돌면, 한쪽의 리스너가 다른 쪽의 "리스너 없음" 관측을 오염시킨다. 같은 컬렉션 + 병렬
/// 비활성으로 순차 실행을 강제해 전역 상태 간섭을 없앤다.
/// </remarks>
[CollectionDefinition("Tracing", DisableParallelization = true)]
public sealed class TracingCollection
{
}

/// <summary>
/// 분산 추적 미들웨어(ADR-0022)를 검증한다 — 디스패치 span 이 태그·상태와 함께 나고,
/// 리스너가 없으면 데코레이터가 async 래퍼 없이 사라진다(fast-path).
/// </summary>
[Collection("Tracing")]
public sealed class TracingTests
{
    private static MessageContext MakeContext(ushort messageId)
    {
        FakeConnection connection = new();
        MessageContext context = new(connection);
        context.BeginFrame(
            new MessageEnvelope(new MessageId(messageId), FrameFlags.None, 0),
            default,
            receivedAt: default,
            CancellationToken.None);

        return context;
    }

    private static ActivityListener CaptureSpans(List<Activity> captured)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = static source => source.Name == DiagnosticNames.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Add,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Fact]
    public async Task WithListener_CreatesDispatchSpan_WithMessageAndConnectionTags()
    {
        List<Activity> captured = [];
        using ActivityListener listener = CaptureSpans(captured);

        TracingMiddleware middleware = new();
        MessageContext context = MakeContext(100);

        DispatchStatus status = await middleware.InvokeAsync(
            context,
            static _ => ValueTask.FromResult(DispatchStatus.Handled));

        Assert.Equal(DispatchStatus.Handled, status);

        Activity span = Assert.Single(captured);
        Assert.Equal(ActivityNames.Dispatch, span.OperationName);
        Assert.Equal(ActivityKind.Server, span.Kind);
        Assert.Equal((ushort)100, (ushort)span.GetTagItem(TagNames.MessageId)!);
        Assert.Equal("conn:7/1", span.GetTagItem(TagNames.ConnectionId));

        // 정상 처리는 상태를 설정하지 않는다 — 오류 span 만 Error 로 필터링된다.
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
    }

    [Fact]
    public async Task RejectedStatus_SetsErrorStatus_AndErrorCodeTag()
    {
        List<Activity> captured = [];
        using ActivityListener listener = CaptureSpans(captured);

        TracingMiddleware middleware = new();
        MessageContext context = MakeContext(200);

        // next 가 거부를 돌려준다 — 미들웨어가 이걸 오류 span 으로 표시해야 한다.
        DispatchStatus status = await middleware.InvokeAsync(
            context,
            static _ => ValueTask.FromResult(DispatchStatus.RejectedByPolicy));

        Assert.Equal(DispatchStatus.RejectedByPolicy, status);

        Activity span = Assert.Single(captured);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(DispatchStatus.RejectedByPolicy), span.GetTagItem(TagNames.ErrorCode));
    }

    [Fact]
    public async Task NoListener_CreatesNoSpan_AndPassesThrough()
    {
        // 리스너가 없으면 span 을 만들지 않고 next 를 그대로 통과시킨다(fast-path). 관측
        // 가능한 계약은 "디스패치 도중 활성 span 이 없다 + 처리가 그대로 진행된다"이다.
        // async 래퍼 제거(동기 반환)라는 내부 최적화는 벤치로 방어한다(리스너 없음 ≈ 기준선).
        TracingMiddleware middleware = new();
        MessageContext context = MakeContext(100);
        bool nextCalled = false;
        Activity? currentDuringNext = null;

        ValueTask<DispatchStatus> pending = middleware.InvokeAsync(
            context,
            _ =>
            {
                nextCalled = true;
                currentDuringNext = Activity.Current;
                return ValueTask.FromResult(DispatchStatus.Handled);
            });

        // 동기 완료 — 리스너가 없을 때 대기 지점이 없다는 뜻이다.
        Assert.True(pending.IsCompletedSuccessfully);
        DispatchStatus status = await pending;

        Assert.True(nextCalled);
        Assert.Null(currentDuringNext);
        Assert.Equal(DispatchStatus.Handled, status);
    }

    /// <summary>추적 태그 검증에 필요한 최소 <see cref="IConnection"/> — ID 만 의미가 있다.</summary>
    private sealed class FakeConnection : IConnection
    {
        private static readonly Pipe DummyPipe = new();

        public ConnectionId Id => new(7, 1);

        public PipeReader Input => DummyPipe.Reader;

        public PipeWriter Output => DummyPipe.Writer;

        public IFeatureCollection Features { get; } = new FeatureCollection(capacity: 0);

        public CancellationToken ConnectionClosed => CancellationToken.None;

        public void Abort(in ConnectionCloseInfo info)
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}
