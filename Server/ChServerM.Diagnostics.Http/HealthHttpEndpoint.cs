using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChServerM.Diagnostics.Http;

/// <summary>
/// 헬스 체크를 HTTP 로 노출하는 admin 엔드포인트 (Phase 11 관측, ADR-0024).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 오케스트레이터(k8s 등)는 HTTP GET 으로 프로브한다 —
/// <c>GET /healthz</c>(liveness)·<c>GET /readyz</c>(readiness). 이 엔드포인트가 헬스 보고서를
/// HTTP 상태코드로 번역한다: 정상·저하 → <c>200</c>, 비정상 → <c>503</c>. 데이터 평면과 다른
/// admin 포트(<see cref="HealthHttpOptions.Prefix"/>)에서 별도로 돈다.
/// </para>
/// <para>
/// <b>Hosting 을 참조하지 않는다.</b> 헬스 소스를 델리게이트
/// (<see cref="Func{T1,T2,TResult}"/> 프로브)로 받으므로 이 어댑터는 Core 만 참조한다 —
/// <c>server.Health.CheckHealthAsync</c> 를 넘기는 것이 표준 사용법이지만, 임의의 헬스 소스에
/// 재사용된다(ADR-0024).
/// </para>
/// <para>
/// <b>왜 HttpListener 인가(ADR-0024).</b> 2개 라우트 GET 헬스 엔드포인트에 Kestrel(ASP.NET Core
/// 호스팅 모델)은 과하다. <see cref="HttpListener"/> 는 BCL 공유 프레임워크라 패키지 의존이 0이고,
/// 이 용도에 충분하다. 응답 본문은 <b>평문</b>이다 — 프로브는 상태코드만 보므로 JSON(과 그
/// 직렬화·AOT 비용)을 들이지 않는다. 본문은 사람이 <c>curl</c> 로 볼 때의 디버그 편의다.
/// </para>
/// <para>
/// <b>항목별 격리.</b> 한 요청의 예외(프로브 실패·연결 끊김)가 accept 루프를 죽이지 않는다 —
/// 500 으로 응답하고 루프는 계속한다(소비 루프 항목 격리와 같은 원칙, CLAUDE.md 9.2). 프로브는
/// 순차 처리한다: 헬스 요청은 저빈도라 동시성이 불필요하고, 순차가 단순·결정적이다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> <see cref="Start"/>·<see cref="StopAsync"/>·<see cref="DisposeAsync"/> 는
/// 조립·종료 스레드에서 한 번씩 부른다. accept 루프는 전용 백그라운드 태스크에서 돈다.
/// </para>
/// </remarks>
public sealed class HealthHttpEndpoint : IAsyncDisposable
{
    private readonly Func<HealthProbe, CancellationToken, ValueTask<HealthReport>> _probe;
    private readonly string _prefix;
    private readonly string _livenessPath;
    private readonly string _readinessPath;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();

    private Task? _acceptLoop;
    private int _started;
    private int _disposed;

    /// <summary>프로브 델리게이트와 설정으로 엔드포인트를 만든다.</summary>
    /// <param name="probe">
    /// 프로브를 받아 헬스 보고서를 내는 델리게이트. 보통 <c>server.Health.CheckHealthAsync</c>.
    /// </param>
    /// <param name="options">설정. <see langword="null"/>이면 기본값(루프백 <c>:8081</c>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/>가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    public HealthHttpEndpoint(
        Func<HealthProbe, CancellationToken, ValueTask<HealthReport>> probe,
        HealthHttpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(probe);

        options ??= new HealthHttpOptions();
        options.Validate();

        _probe = probe;
        _prefix = options.Prefix;
        _livenessPath = options.LivenessPath;
        _readinessPath = options.ReadinessPath;
        _listener.Prefixes.Add(_prefix);
    }

    /// <summary>수신을 시작한다.</summary>
    /// <exception cref="InvalidOperationException">이미 시작했을 때.</exception>
    /// <exception cref="ObjectDisposedException">이미 정리됐을 때.</exception>
    /// <exception cref="HttpListenerException">주소를 바인드할 수 없을 때(URL ACL·권한·포트 충돌).</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("엔드포인트가 이미 시작됐다.");
        }

        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>수신을 멈추고 accept 루프가 끝나기를 기다린다.</summary>
    public async ValueTask StopAsync()
    {
        if (!_stopping.IsCancellationRequested)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }

        // Stop 이 GetContextAsync 를 튕겨내 루프를 깨운다.
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _listener.Close();
        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                // 리스너가 멈췄다(종료). 정상 경로다.
                return;
            }
            catch (ObjectDisposedException)
            {
                // 종료 중에 리스너가 닫혔다.
                return;
            }
            catch (InvalidOperationException)
            {
                // 리스너가 시작되지 않은 상태로 튕겼다(종료 경합). 멈춘다.
                return;
            }

            await HandleAsync(context).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        HttpListenerResponse response = context.Response;
        int statusCode;
        string body;

        try
        {
            (statusCode, body) = await ResolveAsync(context.Request).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 한 요청의 예외가 accept 루프를 죽이지 않는다 — 500 으로 응답하고 계속.
        catch (Exception)
#pragma warning restore CA1031
        {
            statusCode = 500;
            body = "health probe failed\n";
        }

        try
        {
            response.StatusCode = statusCode;
            response.ContentType = "text/plain; charset=utf-8";
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 클라이언트가 응답 전에 끊었을 수 있다 — 무시하고 다음 요청으로.
        catch (Exception)
#pragma warning restore CA1031
        {
            // 응답을 쓸 수 없다(연결 끊김 등). 아래 finally 가 정리한다.
        }
        finally
        {
            response.Close();
        }
    }

    private async ValueTask<(int StatusCode, string Body)> ResolveAsync(HttpListenerRequest request)
    {
        if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return (405, "method not allowed\n");
        }

        string path = request.Url?.AbsolutePath ?? "/";
        HealthProbe? probe = MatchProbe(path);
        if (probe is null)
        {
            return (404, "not found\n");
        }

        HealthReport report = await _probe(probe.Value, _stopping.Token).ConfigureAwait(false);

        // 저하(Degraded)는 경고이지 실패가 아니다 — 프로브는 통과(200)시킨다. 비정상만 503.
        int statusCode = report.Status == HealthStatus.Unhealthy
            ? (int)HttpStatusCode.ServiceUnavailable
            : (int)HttpStatusCode.OK;

        return (statusCode, FormatBody(report));
    }

    private HealthProbe? MatchProbe(string path)
    {
        if (string.Equals(path, _livenessPath, StringComparison.Ordinal))
        {
            return HealthProbe.Liveness;
        }

        if (string.Equals(path, _readinessPath, StringComparison.Ordinal))
        {
            return HealthProbe.Readiness;
        }

        return null;
    }

    private static string FormatBody(HealthReport report)
    {
        // 첫 줄은 집계 상태, 이후 체크별 한 줄. curl 로 볼 때의 디버그 편의 — 프로브는 상태코드만 본다.
        StringBuilder builder = new();
        builder.Append(report.Status).Append('\n');

        foreach (HealthReportEntry entry in report.Entries)
        {
            builder.Append(entry.Name).Append(": ").Append(entry.Status);
            if (!string.IsNullOrEmpty(entry.Description))
            {
                builder.Append(" (").Append(entry.Description).Append(')');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }
}
