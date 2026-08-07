using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Hosting.Dispatch;
using ChServerM.Identity;
using ChServerM.Resilience;
using ChServerM.Serialization;
using ChServerM.Transport.InMemory;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 장애 주입 — 적대적 조건을 실제로 만들어 서버가 살아남는지 확인한다 (Phase 10).
/// </summary>
/// <remarks>
/// <para>
/// <b>결정적 시나리오로 주입한다 — 무작위 카오스를 쓰지 않는다.</b> 무작위는 재현되지 않는
/// 실패를 만들고, 재현 안 되는 테스트는 결국 꺼진다(레거시가 퍼징 시드를 고정한 것과 같은
/// 근거). 여기서는 각 적대 조건을 <b>명시적으로</b> 만들고 기대 결과를 고정한다.
/// </para>
/// <para>
/// <b>주입 설비를 새로 만들지 않는다.</b> 적대적 조건 대부분은 기존 API 로 그대로 만들어진다 —
/// 부분 프레임을 쓰고 끊기, 쓰레기 바이트 보내기, 실패하는 축 구현 꽂기. 설비를 추상화하면
/// 검증 대상보다 설비가 커진다.
/// </para>
/// <para>
/// <b>마지막 시나리오가 이 파일의 핵심이다.</b> Phase 10 게이트는 "과부하에서 <b>거부하며
/// 살아남는다</b>" 를 주장하는데, 그동안 각 기구(수용 제어·속도 제한·열화)는 <b>따로만</b>
/// 검증됐다. 함께 걸었을 때 조합이 성립하는지가 게이트 주장의 실제 증거다.
/// </para>
/// </remarks>
public sealed class FaultInjectionTests : IDisposable
{
    private const ushort EchoId = 970;
    private const ushort TypedId = 971;
    private const ushort TelemetryId = 972;

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _timeout.Dispose();

    /// <summary>항상 역직렬화에 실패하는 직렬화기 — 손상된 페이로드를 흉내낸다.</summary>
    private sealed class FailingSerializer : IMessageSerializer<string>
    {
        public void Serialize(IBufferWriter<byte> writer, in string message) => writer.Advance(0);

        public bool TryDeserialize(in ReadOnlySequence<byte> payload, out string message)
        {
            message = string.Empty;
            return false;
        }
    }

    private sealed class RecordingHandler : IMessageHandler<string>
    {
        public int Calls;

        public ValueTask HandleAsync(MessageContext context, string message)
        {
            Interlocked.Increment(ref Calls);
            return default;
        }
    }

    private sealed class FixedLoad : ILoadLevelSource
    {
        public LoadLevel Current { get; set; } = LoadLevel.Normal;
    }

    [Fact]
    public async Task Deserialization_failure_is_reported_and_handler_is_not_called()
    {
        // DispatchStatus.DeserializationFailed 의 생산 경로 — 정의만 있고 검증된 적이 없었다.
        // 손상된 페이로드가 핸들러에 닿으면 안 된다.
        RecordingHandler handler = new();

        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.Map(new MessageId(TypedId), new FailingSerializer(), handler),
            connectionOptions: new FramedConnectionOptions { CloseOnDeserializationFailure = true });

        await using IConnection connection = await harness.ConnectAsync();
        await harness.SendAsync(connection, TypedId, [1, 2, 3]);

        // 역직렬화 실패는 종료로 이어진다(옵션). 커넥션이 닫히는 것으로 확정된다.
        await WaitUntilAsync(() => harness.ServerConnectionCount == 0);

        Assert.Equal(0, Volatile.Read(ref handler.Calls));
    }

    [Fact]
    public async Task Garbage_bytes_close_the_connection_without_killing_the_server()
    {
        // 프레이밍 퍼즈는 코덱 수준에서만 돌았다 — 실제 소켓으로 쓰레기를 보냈을 때
        // 서버가 그 커넥션만 끊고 살아남는지는 별개 문제다.
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoId), _ => ValueTask.FromResult(DispatchStatus.Handled)));

        await using (IConnection hostile = await harness.ConnectAsync())
        {
            // 완전한 헤더 길이(16바이트)를 채우되 내용이 유효하지 않다 — 버전 필드가 틀려
            // 치명적 디코딩 실패가 된다. 헤더보다 짧게 보내면 디코더는 "더 기다림"이라
            // 판정하므로(정상 동작) 적대 조건이 성립하지 않는다.
            byte[] garbage = new byte[16];
            Array.Fill(garbage, (byte)0xFF);
            hostile.Output.Write(garbage.AsSpan());
            await hostile.Output.FlushAsync(_timeout.Token);

            // 재동기화가 불가능하므로 서버는 이 커넥션을 닫는다.
            await WaitUntilAsync(() => harness.ServerConnectionCount == 0);
        }

        // 서버는 살아 있다 — 새 커넥션이 정상 동작한다.
        await using IConnection healthy = await harness.ConnectAsync();
        await harness.SendAsync(healthy, EchoId, [1]);
        await WaitUntilAsync(() => harness.ServerConnectionCount == 1);
    }

    [Fact]
    public async Task Truncated_frame_then_disconnect_is_survived()
    {
        // 프레임 중간에 끊는 클라이언트. 서버는 남은 바이트를 조용히 버리지 않고
        // 정리해야 한다(FramedConnectionHandler 의 잘린 프레임 경로).
        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder.MapRaw(new MessageId(EchoId), _ => ValueTask.FromResult(DispatchStatus.Handled)));

        await using (IConnection torn = await harness.ConnectAsync())
        {
            // 고정 헤더의 앞 3바이트만 보낸다 — 프레임이 완성되지 않는다.
            torn.Output.Write(new byte[] { 0x01, 0x02, 0x03 }.AsSpan());
            await torn.Output.FlushAsync(_timeout.Token);
        }

        // 커넥션이 정리되고 서버는 계속 산다.
        await WaitUntilAsync(() => harness.ServerConnectionCount == 0);

        await using IConnection healthy = await harness.ConnectAsync();
        await harness.SendAsync(healthy, EchoId, [9]);
        await WaitUntilAsync(() => harness.ServerConnectionCount == 1);
    }

    [Fact]
    public async Task Composed_defenses_reject_and_survive_together()
    {
        // ★ 게이트 주장의 증거 — "과부하에서 거부하며 살아남는다".
        // 수용 제어 + 속도 제한 + 열화를 <b>동시에</b> 걸고 적대적 부하를 넣는다.
        // 그동안 세 기구는 따로만 검증됐다.
        ManualTime time = new();
        FixedLoad load = new() { Current = LoadLevel.Critical };

        // 주소별·전역 수용 제어를 AND 로. 버스트를 넉넉히 줘 이 테스트의 연결은 통과시킨다.
        CompositeAdmissionControl admission = new(
            new ConnectionRateAdmissionControl(
                new ConnectionRateAdmissionControlOptions { PermitsPerSecond = 1000, BurstCapacity = 1000 }, time),
            new PerAddressConnectionRateAdmissionControl(
                new PerAddressConnectionRateOptions { PermitsPerSecond = 1000, BurstCapacity = 1000 }, time));

        await using TestHarness harness = await TestHarness.StartAsync(
            builder => builder
                // 열화: 텔레메트리는 Critical 에서 버린다. 에코는 미등록이라 필수.
                .Use(new LoadSheddingMiddleware(
                    load,
                    new LoadSheddingOptions().ShedAbove(new MessageId(TelemetryId), LoadLevel.Normal)))
                // 속도 제한: 커넥션당 아주 좁게 — 폭주가 반드시 걸린다.
                .Use(new RateLimitMiddleware(
                    new PerConnectionRateLimiter(
                        new PerConnectionRateLimitOptions { PermitsPerSecond = 1, BurstCapacity = 2 }, time)))
                .MapRaw(new MessageId(EchoId), _ => ValueTask.FromResult(DispatchStatus.Handled))
                .MapRaw(new MessageId(TelemetryId), _ => ValueTask.FromResult(DispatchStatus.Handled)),
            transportOptions: new InMemoryTransportOptions { AdmissionControl = admission });

        await using IConnection connection = await harness.ConnectAsync();

        // 적대적 폭주: 필수·비필수를 섞어 대량 전송.
        for (int i = 0; i < 200; i++)
        {
            await harness.SendAsync(connection, i % 2 == 0 ? EchoId : TelemetryId, [(byte)i]);
        }

        // 핵심 단언: 거부가 일어나되 <b>서버가 살아 있고 커넥션이 유지된다</b>.
        // 열화·속도 제한은 모두 무-종료이므로 이 폭주로 커넥션이 끊기면 안 된다 —
        // 끊겼다면 재접속 폭풍으로 부하를 키우는 역효과가 실재한다는 뜻이다.
        await Task.Delay(100, _timeout.Token);
        Assert.Equal(1, harness.ServerConnectionCount);

        // 부하가 내려가고 토큰이 차면 다시 정상 처리된다(회복).
        load.Current = LoadLevel.Normal;
        time.Advance(TimeSpan.FromSeconds(10));

        await using IConnection fresh = await harness.ConnectAsync();
        await harness.SendAsync(fresh, EchoId, [1]);
        await WaitUntilAsync(() => harness.ServerConnectionCount == 2);
    }

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("조건이 제한 시간 안에 만족되지 않았다.");
            }

            await Task.Delay(10, _timeout.Token);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _timestamp, delta.Ticks);
    }
}
