using System;
using System.Buffers;
using System.Collections.Generic;
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

/// <summary>
/// 디스패치 실패 분류 태그의 계약을 고정한다 (감사 2026-08-18 H-4/O-9).
/// </summary>
/// <remarks>
/// <para>
/// <b>O-9 — 태그 이름.</b> 실패 카운터의 분류 태그는 <c>error_code</c>(<see cref="ErrorCode"/>
/// 값 계약)가 아니라 전용 <c>dispatch_status</c> 다. 한 태그에 두 값 체계가 섞이면
/// 대시보드 필터가 성립하지 않는다.
/// </para>
/// <para>
/// <b>H-4 — 무할당.</b> 거부·실패는 정확히 과부하 시 프레임마다 발생하는 경로라
/// <c>enum.ToString()</c> 의 프레임당 할당이 금지된다. 상태명이 <b>호출마다 같은 문자열
/// 참조</b>인 것(정적 캐시)을 참조 동일성으로 확인한다 — 캐시에서 빠진 enum 멤버는
/// <c>ToString()</c> 폴백(호출마다 새 문자열)이라 이 검사가 누락도 잡는다.
/// </para>
/// </remarks>
public sealed class DispatchStatusTagTests
{
    [Fact]
    public async Task Failure_is_tagged_with_dispatch_status_not_error_code()
    {
        CapturingSink sink = new();
        MetricsMiddleware middleware = new(sink);

        await middleware.InvokeAsync(
            NewContext(), _ => ValueTask.FromResult(DispatchStatus.HandlerNotFound));

        (string tagName, string? tagValue) = Assert.Single(sink.FailureTags);
        Assert.Equal(TagNames.DispatchStatus, tagName);
        Assert.Equal(nameof(DispatchStatus.HandlerNotFound), tagValue);
    }

    [Fact]
    public async Task Handled_emits_no_failure_counter()
    {
        CapturingSink sink = new();
        MetricsMiddleware middleware = new(sink);

        await middleware.InvokeAsync(NewContext(), _ => ValueTask.FromResult(DispatchStatus.Handled));

        Assert.Empty(sink.FailureTags);
    }

    [Fact]
    public async Task Every_failure_status_name_is_cached_and_correct()
    {
        // 모든 상태에 대해 ① 이름이 enum 멤버명과 같고 ② 두 호출이 같은 참조를 돌려주는지.
        // ②가 캐시의 증거다 — ToString() 폴백이면 호출마다 새 문자열이라 참조가 다르다.
        // 새 enum 멤버가 캐시 배열에 누락되면 이 테스트가 잡는다(DispatchStatusNames 문서).
        foreach (DispatchStatus status in Enum.GetValues<DispatchStatus>())
        {
            if (status == DispatchStatus.Handled)
            {
                continue;
            }

            CapturingSink sink = new();
            MetricsMiddleware middleware = new(sink);
            DispatchStatus captured = status;

            await middleware.InvokeAsync(NewContext(), _ => ValueTask.FromResult(captured));
            await middleware.InvokeAsync(NewContext(), _ => ValueTask.FromResult(captured));

            Assert.Equal(2, sink.FailureTags.Count);
            Assert.Equal(status.ToString(), sink.FailureTags[0].Value);
            Assert.Same(sink.FailureTags[0].Value, sink.FailureTags[1].Value);
        }
    }

    private static MessageContext NewContext()
    {
        MessageContext context = new(new StubConnection());
        context.BeginFrame(
            new MessageEnvelope(new MessageId(1), FrameFlags.None, 0),
            new ReadOnlySequence<byte>(Array.Empty<byte>()),
            receivedAt: default,
            CancellationToken.None);
        return context;
    }

    /// <summary>실패 카운터의 태그를 <b>문자열 참조 그대로</b> 붙잡아 두는 싱크.</summary>
    private sealed class CapturingSink : IMetricsSink
    {
        public List<(string Name, string? Value)> FailureTags { get; } = [];

        public void Count(string name, long delta, ReadOnlySpan<MetricTag> tags)
        {
            if (name != MetricNames.DispatchFailures)
            {
                return;
            }

            foreach (MetricTag tag in tags)
            {
                FailureTags.Add((tag.Name, tag.Value));
            }
        }

        public void Record(string name, double value, ReadOnlySpan<MetricTag> tags)
        {
        }

        public void AdjustGauge(string name, long delta, ReadOnlySpan<MetricTag> tags)
        {
        }
    }

    /// <summary>미들웨어 호출에 필요한 최소 커넥션.</summary>
    private sealed class StubConnection : IConnection
    {
        private readonly Pipe _pipe = new();

        public ConnectionId Id => new(1, 0);

        public PipeReader Input => _pipe.Reader;

        public PipeWriter Output => _pipe.Writer;

        public IFeatureCollection Features { get; } = new FeatureCollection(capacity: 2);

        public CancellationToken ConnectionClosed => CancellationToken.None;

        public void Abort(in ConnectionCloseInfo info)
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}
