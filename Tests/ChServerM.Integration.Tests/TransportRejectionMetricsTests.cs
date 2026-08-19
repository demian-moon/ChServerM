using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Resilience;
using ChServerM.Transport.Http;
using ChServerM.Transport.Quic;
using ChServerM.Transport.WebSocket;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// HTTP·WebSocket·QUIC 의 수용 제어·거부 메트릭 대칭 검증 — 감사 2026-08-18 T-5.
/// </summary>
/// <remarks>
/// TCP·인메모리만 갖고 있던 <c>IAdmissionControl</c> 호출과
/// <see cref="MetricNames.ConnectionsRejected"/> 방출이 세 전송에도 배선됐음을 고정한다 —
/// 재접속 스톰 방어와 드롭 관측(CLAUDE.md 9.6)이 전송 선택에 따라 사라지면 조립 가능성이
/// 관측·방어 축에서 깨진다.
/// </remarks>
public sealed class TransportRejectionMetricsTests : IDisposable
{
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _timeout.Dispose();

    [Fact]
    public async Task Http_admission_rejection_returns_503_and_emits_metric()
    {
        RecordingSink sink = new();
        HttpTransportOptions options = new() { AdmissionControl = new DenyAll(), MetricsSink = sink };

        await using HttpServerTransport server = new(new IPEndPoint(IPAddress.Loopback, 0), options);
        await server.BindAsync(new CompletedHandler());

        EndPoint endPoint = server.LocalEndPoint
            ?? throw new InvalidOperationException("바인드 후에도 LocalEndPoint 가 없다.");

        await using HttpClientTransport client = new(options);

        // 503 은 클라이언트 전송에서 연결 시점 예외로 드러난다.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.ConnectAsync(endPoint, _timeout.Token));

        Assert.True(sink.CountOf(MetricNames.ConnectionsRejected, "admission") >= 1,
            "수용 거부가 ConnectionsRejected(admission) 메트릭으로 관측되지 않았다.");
    }

    [Fact]
    public async Task WebSocket_admission_rejection_returns_503_and_emits_metric()
    {
        RecordingSink sink = new();
        WebSocketTransportOptions options = new() { AdmissionControl = new DenyAll(), MetricsSink = sink };

        await using WebSocketServerTransport server = new(new IPEndPoint(IPAddress.Loopback, 0), options);
        await server.BindAsync(new CompletedHandler());

        EndPoint endPoint = server.LocalEndPoint
            ?? throw new InvalidOperationException("바인드 후에도 LocalEndPoint 가 없다.");

        await using WebSocketClientTransport client = new(options);

        // 503 은 업그레이드 실패 예외로 드러난다.
        await Assert.ThrowsAnyAsync<System.Net.WebSockets.WebSocketException>(
            async () => await client.ConnectAsync(endPoint, _timeout.Token));

        Assert.True(sink.CountOf(MetricNames.ConnectionsRejected, "admission") >= 1,
            "수용 거부가 ConnectionsRejected(admission) 메트릭으로 관측되지 않았다.");
    }

    [Fact]
    public async Task WebSocket_connection_limit_rejection_emits_metric()
    {
        RecordingSink sink = new();
        WebSocketTransportOptions options = new() { MaxConnections = 1, MetricsSink = sink };
        HoldingHandler handler = new();

        await using WebSocketServerTransport server = new(new IPEndPoint(IPAddress.Loopback, 0), options);
        await server.BindAsync(handler);

        EndPoint endPoint = server.LocalEndPoint
            ?? throw new InvalidOperationException("바인드 후에도 LocalEndPoint 가 없다.");

        await using WebSocketClientTransport client = new(options);
        IConnection first = await client.ConnectAsync(endPoint, _timeout.Token);

        try
        {
            await Assert.ThrowsAnyAsync<System.Net.WebSockets.WebSocketException>(
                async () => await client.ConnectAsync(endPoint, _timeout.Token));

            Assert.True(sink.CountOf(MetricNames.ConnectionsRejected, "connection_limit") >= 1,
                "정적 상한 거부가 ConnectionsRejected(connection_limit) 메트릭으로 관측되지 않았다.");
        }
        finally
        {
            handler.Release();
            await first.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Quic_admission_rejection_aborts_stream_and_emits_metric()
    {
        Skip.If(
            !System.Net.Quic.QuicListener.IsSupported,
            "이 환경은 QUIC 을 지원하지 않는다 (msquic/TLS 스택).");

#pragma warning disable CA1416 // 바로 위의 IsSupported 게이트가 플랫폼 지원을 확인했다.
        RecordingSink sink = new();
        using System.Security.Cryptography.X509Certificates.X509Certificate2 certificate = CreateTestCertificate();
        QuicTransportOptions options = new()
        {
            ServerCertificate = certificate,
            AdmissionControl = new DenyAll(),
            MetricsSink = sink,

            // 테스트 전용 신뢰 정책 — 실행마다 새 자가서명 인증서라 검증할 신뢰 체계가 없다.
#pragma warning disable CA5359 // 위 주석 참조 — 테스트 전용 자가서명 인증서라 검증 대상이 없다.
            RemoteCertificateValidation = static (_, _, _, _) => true,
#pragma warning restore CA5359
        };

        await using QuicServerTransport server = new(new IPEndPoint(IPAddress.Loopback, 0), options);
        await server.BindAsync(new CompletedHandler());

        EndPoint endPoint = server.LocalEndPoint
            ?? throw new InvalidOperationException("바인드 후에도 LocalEndPoint 가 없다.");

        await using QuicClientTransport client = new(options);

        // QUIC 은 스트림이 첫 데이터와 함께 상대에 드러난다 — 연결 후 한 바이트를 밀어
        // 서버의 스트림 수락(그리고 수용 거부)을 트리거한다. 거부는 스트림 중단으로
        // 비동기 관측되므로 예외 여부는 고정하지 않고 메트릭 방출만 검증한다.
        try
        {
            IConnection connection = await client.ConnectAsync(endPoint, _timeout.Token);
            await connection.Output.WriteAsync(new byte[] { 1 }, _timeout.Token);
            await connection.DisposeAsync();
        }
#pragma warning disable CA1031 // 거부된 스트림의 클라이언트 측 실패 형태는 타이밍에 따라 갈린다 — 검증 대상이 아니다.
        catch (Exception)
        {
            // 서버가 이미 스트림을 중단했을 수 있다.
        }
#pragma warning restore CA1031

        // 거부는 서버 쪽에서 비동기로 일어난다 — 방출될 때까지 짧게 폴링한다.
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (sink.CountOf(MetricNames.ConnectionsRejected, "admission") < 1)
        {
            Assert.True(DateTime.UtcNow < deadline,
                "수용 거부가 ConnectionsRejected(admission) 메트릭으로 관측되지 않았다.");
            await Task.Delay(50, _timeout.Token);
        }
#pragma warning restore CA1416
    }

    /// <summary>QUIC 테스트용 자가서명 인증서 — serverAuth EKU + PFX 왕복(ADR-0060 함정 반영).</summary>
    private static System.Security.Cryptography.X509Certificates.X509Certificate2 CreateTestCertificate()
    {
        using System.Security.Cryptography.RSA rsa = System.Security.Cryptography.RSA.Create(2048);
        System.Security.Cryptography.X509Certificates.CertificateRequest request = new(
            "CN=chsm-test",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
                [new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        using System.Security.Cryptography.X509Certificates.X509Certificate2 ephemeral =
            request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));

        return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx),
            password: null);
    }

    /// <summary>모든 연결을 거부하는 수용 제어 — 거부 경로 전용 스텁.</summary>
    private sealed class DenyAll : IAdmissionControl
    {
        public AdmissionDecision TryAdmit(EndPoint? remoteEndPoint) => AdmissionDecision.Reject("test");
    }

    /// <summary>즉시 끝나는 핸들러 — 수락 경로에 도달하면 안 되는 테스트용.</summary>
    private sealed class CompletedHandler : IConnectionHandler
    {
        public Task RunAsync(IConnection connection) => Task.CompletedTask;
    }

    /// <summary>놓아줄 때까지 커넥션을 붙들어 두는 핸들러 — 정적 상한 점유용.</summary>
    private sealed class HoldingHandler : IConnectionHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RunAsync(IConnection connection) => _release.Task;

        public void Release() => _release.TrySetResult();
    }

    /// <summary>이름·사유 태그별 카운터를 기록하는 테스트용 싱크(수용 제어 종단 테스트와 같은 형태).</summary>
    private sealed class RecordingSink : IMetricsSink
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);

        public long CountOf(string metric, string reason)
        {
            lock (_lock)
            {
                return _counts.GetValueOrDefault(metric + "|" + reason);
            }
        }

        public void Count(string name, long delta, ReadOnlySpan<MetricTag> tags)
        {
            string reason = "";
            foreach (MetricTag tag in tags)
            {
                if (tag.Name == TagNames.CloseReason)
                {
                    reason = tag.Value ?? "";
                }
            }

            lock (_lock)
            {
                string key = name + "|" + reason;
                _counts[key] = _counts.GetValueOrDefault(key) + delta;
            }
        }

        public void Record(string name, double value, ReadOnlySpan<MetricTag> tags)
        {
        }

        public void AdjustGauge(string name, long delta, ReadOnlySpan<MetricTag> tags)
        {
        }
    }
}
