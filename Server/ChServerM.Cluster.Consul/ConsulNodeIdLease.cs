using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Identity;

namespace ChServerM.Cluster.Consul;

/// <summary>
/// 노드 번호를 Consul KV 잠금으로 <b>임차</b>한다 — 겹친 번호를 <b>기동 실패로 드러낸다</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 번호 유일성이 지금까지 조립 시점에만 강제됐다.</b>
/// <see cref="ClusterView"/> 는 <b>한 목록 안의</b> 중복을 잡지만(ADR-0048), 서로 다른
/// 노드가 <b>각자 다른 목록</b>을 들고 기동하면 아무도 겹침을 보지 못한다. 그러면
/// <see cref="ObjectId"/> 가 <b>조용히 충돌</b>하고, 증상은 "가끔 엉뚱한 객체가 나온다" 로
/// 나타나 원인에서 아주 멀어진다. 이 타입이 그 침묵을 없앤다.
/// </para>
///
/// <para>
/// <b>⚠ 번호를 배정하지 않는다. 확인한다.</b> 번호를 어디서 얻는지는 배포가 정한다
/// (K8s StatefulSet 서수·Nomad 할당 인덱스·설정). 프레임워크가 배정하려면 "몇 번까지
/// 쓰는가" 를 알아야 하는데 그것은 배포가 아는 값이고, 재시작마다 번호가 바뀌면
/// 운영자가 로그에서 노드를 추적할 수 없게 된다.
/// </para>
///
/// <para>
/// <b>⚠⚠ 유일성은 <i>세션이 살아 있는 동안만</i> 보장된다. 이것은 상호 배제가 아니다.</b>
/// 우리 세션이 만료됐는데 <b>우리는 아직 돌고 있는</b> 구간이 존재한다 — 만료 판정은
/// Consul 이 하고, 우리는 갱신에 실패한 뒤에야 그것을 안다. 그 사이에 다른 노드가 번호를
/// 가져가면 둘이 같은 번호로 동작한다. <see cref="ConsulNodeIdLeaseOptions.LockDelay"/> 가
/// 그 창을 <b>좁히지만 없애지는 못한다</b>. 그래서 <see cref="Lost"/> 를 노출한다 —
/// <b>임차를 잃었을 때 무엇을 할지는 앱이 정한다</b>(보통은 프로세스를 내린다).
/// 프레임워크가 대신 죽이지 않는다.
/// </para>
///
/// <code>
///   await using ConsulNodeIdLease lease = await ConsulNodeIdLease.AcquireAsync(options, logger);
///
///   _ = lease.Lost.ContinueWith(_ => host.StopAsync(), TaskScheduler.Default);
/// </code>
///
/// <para>
/// <b>스레드 규약.</b> 모든 멤버가 스레드 안전하다. 갱신 루프 하나가 배경에서 돈다.
/// </para>
/// </remarks>
public sealed class ConsulNodeIdLease : IAsyncDisposable
{
    private static readonly EventId AcquiredEvent = new(2030, "NodeIdLeaseAcquired");
    private static readonly EventId LostEvent = new(2031, "NodeIdLeaseLost");
    private static readonly EventId RenewFailedEvent = new(2032, "NodeIdLeaseRenewFailed");

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ConsulNodeIdLeaseOptions _options;
    private readonly IServerLogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _sessionId;

    private Task? _renewLoop;
    private int _disposed;

    private ConsulNodeIdLease(
        HttpClient http,
        bool ownsHttp,
        ConsulNodeIdLeaseOptions options,
        IServerLogger logger,
        string sessionId)
    {
        _http = http;
        _ownsHttp = ownsHttp;
        _options = options;
        _logger = logger;
        _sessionId = sessionId;
        NodeId = options.NodeId;
    }

    /// <summary>임차한 노드 번호.</summary>
    public NodeId NodeId { get; }

    /// <summary>
    /// 임차를 <b>잃으면</b> 완료된다. <see cref="DisposeAsync"/> 로 정상 반납해도 완료된다.
    /// </summary>
    /// <remarks>
    /// <b>밀지 않고 기다리게 한다</b>(ADR-0047 과 같은 판단) — 이벤트로 밀면 구독 해제를
    /// 빠뜨린 쪽이 누수가 되고, 느린 구독자를 위한 큐가 필요해진다.
    /// </remarks>
    public Task Lost => _lost.Task;

    /// <summary>번호를 임차한다. <b>이미 남이 들고 있으면 던진다</b>.</summary>
    /// <param name="options">번호·주소·TTL.</param>
    /// <param name="logger">로거.</param>
    /// <param name="httpClient">쓸 클라이언트. <see langword="null"/> 이면 직접 만들고 직접 정리한다.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>살아 있는 임차.</returns>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/> 이다.</exception>
    /// <exception cref="InvalidOperationException">
    /// 설정이 성립하지 않거나, Consul 에 닿을 수 없거나, <b>번호가 이미 쓰이고 있다</b>.
    /// </exception>
    /// <remarks>
    /// <b>실패하면 기동을 멈추는 것이 맞다.</b> 겹친 번호로 계속 가면 <see cref="ObjectId"/>
    /// 가 조용히 충돌하고, 그 증상은 원인에서 아주 멀다. 예외 메시지에 <b>누가 들고 있는지</b>
    /// 를 실어 운영자가 곧바로 찾을 수 있게 한다.
    /// </remarks>
    public static async ValueTask<ConsulNodeIdLease> AcquireAsync(
        ConsulNodeIdLeaseOptions options,
        IServerLogger logger,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        options.Validate();

        HttpClient http = httpClient ?? new HttpClient();
        bool ownsHttp = httpClient is null;
        string? sessionId = null;

        try
        {
            sessionId = await CreateSessionAsync(http, options, cancellationToken).ConfigureAwait(false);

            bool acquired = await TryAcquireAsync(http, options, sessionId, cancellationToken)
                .ConfigureAwait(false);

            if (!acquired)
            {
                string? holder = await ReadHolderAsync(http, options, cancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"노드 번호 {options.NodeId.Value} 는 이미 임차돼 있다"
                    + (holder is null ? "." : $" (보유자: {holder}).")
                    + " 겹친 번호로 기동하면 ObjectId 가 조용히 충돌한다 — 배포가 준 번호를 확인한다.");
            }

            ConsulNodeIdLease lease = new(http, ownsHttp, options, logger, sessionId);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.Log(
                    LogLevel.Information, AcquiredEvent, options.NodeId, null,
                    static (id, _) => $"노드 번호 {id.Value} 를 임차했다.");
            }

            lease._renewLoop = Task.Run(lease.RenewAsync, CancellationToken.None);
            return lease;
        }
        catch
        {
            // ⚠ 세션을 만들었는데 임차에 실패했으면 **그 세션을 지운다**. 안 지우면
            //   TTL 이 만료될 때까지 Consul 에 유령 세션이 남는다.
            if (sessionId is not null)
            {
                await DestroySessionAsync(http, options, sessionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (ownsHttp)
            {
                http.Dispose();
            }

            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>명시적 반납은 즉시 풀린다</b> — <see cref="ConsulNodeIdLeaseOptions.LockDelay"/> 는
    /// 세션 무효화에만 걸리므로, 정상 종료한 노드의 번호는 곧바로 다시 쓸 수 있다.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_renewLoop is not null)
        {
            // 관측만 하고 상한을 둔다 — 갱신 루프가 HTTP 를 기다리는 중일 수 있고,
            // 그때 무한 대기면 종료가 볼모로 잡힌다(ADR-0051 과 같은 판단).
            try
            {
                await _renewLoop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 정상 종료 경로다.
            }
            catch (TimeoutException)
            {
                // 상한을 넘겼다. 아래에서 정리하고 진행한다.
            }
        }

        // 세션을 지우면 잠금도 함께 풀린다(Behavior=delete).
        await DestroySessionAsync(_http, _options, _sessionId, CancellationToken.None).ConfigureAwait(false);

        _lost.TrySetResult();
        _shutdown.Dispose();

        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    /// <summary>세션을 주기적으로 갱신한다. 실패하면 <see cref="Lost"/> 를 완료시킨다.</summary>
    private async Task RenewAsync()
    {
        CancellationToken token = _shutdown.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.EffectiveRenewInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool renewed;

            try
            {
                using HttpResponseMessage response = await _http.PutAsync(
                    new Uri(_options.Address, $"/v1/session/renew/{_sessionId}"),
                    content: null,
                    token).ConfigureAwait(false);

                // 404 = 세션이 이미 사라졌다. 그 순간 잠금도 풀렸다.
                renewed = response.IsSuccessStatusCode;

                if (!renewed)
                {
                    LogRenewFailure($"HTTP {(int)response.StatusCode}");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (HttpRequestException ex)
            {
                // ⚠ 네트워크 흔들림 한 번으로 번호를 포기하지 않는다 — 다음 주기에 다시
                //   시도하고, TTL 이 만료되면 그때 Consul 이 잠금을 푼다. 여기서 성급하게
                //   Lost 를 완료시키면 **멀쩡한 노드가 스스로 내려간다**.
                LogRenewFailure(ex.Message);
                continue;
            }
            catch (TaskCanceledException ex)
            {
                LogRenewFailure(ex.Message);
                continue;
            }

            if (!renewed)
            {
                MarkLost();
                return;
            }
        }
    }

    private void MarkLost()
    {
        if (!_lost.TrySetResult())
        {
            return;
        }

        if (_logger.IsEnabled(LogLevel.Error))
        {
            _logger.Log(
                LogLevel.Error, LostEvent, NodeId, null,
                static (id, _) =>
                    $"노드 번호 {id.Value} 의 임차를 잃었다. 다른 노드가 이 번호를 가져갈 수 있다 — "
                    + "이 프로세스를 내리는 것이 안전하다.");
        }
    }

    private void LogRenewFailure(string reason)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning, RenewFailedEvent, (NodeId, reason), null,
                static (state, _) => $"노드 번호 {state.NodeId.Value} 세션 갱신 실패: {state.reason}");
        }
    }

    private static async Task<string> CreateSessionAsync(
        HttpClient http,
        ConsulNodeIdLeaseOptions options,
        CancellationToken cancellationToken)
    {
        // Behavior=delete — 세션이 무효화되면 키를 지운다. release 로 두면 키가 남아
        // "값은 있는데 주인은 없는" 상태가 되고, 그것을 다시 해석하는 코드가 필요해진다.
        string body = JsonSerializer.Serialize(
            new ConsulSessionRequest
            {
                Name = $"chserverm-node-{options.NodeId.Value}",
                Behavior = "delete",
                Ttl = FormatSeconds(options.SessionTtl),
                LockDelay = FormatSeconds(options.LockDelay),
            },
            ConsulJsonContext.Default.ConsulSessionRequest);

        using StringContent content = new(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;

        try
        {
            response = await http.PutAsync(
                new Uri(options.Address, "/v1/session/create"), content, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Consul 에 닿을 수 없어 노드 번호를 임차하지 못했다: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Consul 세션 생성이 실패했다: HTTP {(int)response.StatusCode}");
            }

            ConsulSessionResponse? session = await JsonSerializer.DeserializeAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                ConsulJsonContext.Default.ConsulSessionResponse,
                cancellationToken).ConfigureAwait(false);

            return session?.Id
                ?? throw new InvalidOperationException("Consul 세션 응답에 ID 가 없다.");
        }
    }

    private static async Task<bool> TryAcquireAsync(
        HttpClient http,
        ConsulNodeIdLeaseOptions options,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using StringContent content = new(options.HolderName, Encoding.UTF8, "text/plain");

        using HttpResponseMessage response = await http.PutAsync(
            KeyUri(options, $"?acquire={sessionId}"), content, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"노드 번호 임차 요청이 실패했다: HTTP {(int)response.StatusCode}");
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // Consul 은 "true"/"false" 를 본문으로 돌려준다. 상태 코드는 둘 다 200 이다 —
        // ⚠ 상태 코드만 보면 **실패를 성공으로 읽는다**(ADR-0051 의 FlushResult 와 같은 모양).
        // 고의 회귀로 확인: 본문을 버리고 true 를 돌려주면 두 노드가 같은 번호를 잡는다.
        return bool.TryParse(body.Trim(), out bool acquired) && acquired;
    }

    /// <summary>누가 들고 있는지 읽는다. 진단 전용이라 실패해도 던지지 않는다.</summary>
    private static async Task<string?> ReadHolderAsync(
        HttpClient http,
        ConsulNodeIdLeaseOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(
                KeyUri(options, "?raw=true"), cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string holder = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(holder) ? null : holder;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static async Task DestroySessionAsync(
        HttpClient http,
        ConsulNodeIdLeaseOptions options,
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http.PutAsync(
                new Uri(options.Address, $"/v1/session/destroy/{sessionId}"),
                content: null,
                cancellationToken).ConfigureAwait(false);

            _ = response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            // 정리 경로다. Consul 에 닿지 못하면 TTL 이 만료시킨다.
        }
        catch (TaskCanceledException)
        {
            // 위와 같다.
        }
    }

    private static Uri KeyUri(ConsulNodeIdLeaseOptions options, string query) =>
        new(options.Address,
            $"/v1/kv/{options.KeyPrefix.Trim('/')}/{options.NodeId.Value.ToString(CultureInfo.InvariantCulture)}{query}");

    private static string FormatSeconds(TimeSpan value) =>
        $"{((int)value.TotalSeconds).ToString(CultureInfo.InvariantCulture)}s";
}
