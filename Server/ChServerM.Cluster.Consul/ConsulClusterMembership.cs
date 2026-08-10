using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Identity;

namespace ChServerM.Cluster.Consul;

/// <summary>
/// Consul 의 <b>블로킹 쿼리</b>로 구성원을 따라가는 <see cref="IClusterMembership"/> 어댑터.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 이 구현이 나오기 전까지 축은 가설이었다</b>(CLAUDE.md 3절).
/// 두 번째 구현이 없으면 인터페이스가 정말 교체 가능한지 알 수 없고, 정적 목록 하나로는
/// <b>바뀌는 구성</b>이라는 이 축의 본질이 한 번도 실행되지 않는다.
/// </para>
///
/// <para>
/// <b>⭐ Consul 의 블로킹 쿼리가 이 축의 계약과 같은 모양이다.</b>
/// <c>GET /v1/health/service/{name}?index=N&amp;wait=T</c> 는 인덱스가 <c>N</c> 보다 커질
/// 때까지 응답을 <b>보류</b>하고, 응답 헤더 <c>X-Consul-Index</c> 로 새 인덱스를 준다 —
/// <see cref="WaitForChangeAsync"/> 의 "세대를 들고 기다린다" 와 같고, <b>밀지 않고
/// 기다린다</b>(ADR-0047)는 판단이 그대로 맞아떨어진다. 폴링 주기라는 손잡이가 없다.
/// </para>
///
/// <para>
/// <b>⚠⚠ 그러나 Consul 의 인덱스를 세대로 <i>쓰지</i> 않는다.</b> 둘은 이름만 닮았다:
/// </para>
/// <list type="bullet">
///   <item>Consul 인덱스는 <c>ulong</c> 이고 <see cref="ClusterView.Generation"/> 은
///     <c>int</c> 다 — 그대로 실으면 <b>잘린다</b>.</item>
///   <item><b>Consul 인덱스는 되돌아갈 수 있다</b>(서버 재시작·복원). 우리 세대는
///     단조 증가여야 하고, 그 위에서 <c>ClusterRouteResolver</c> 의 캐시 교체가 돈다.</item>
///   <item>인덱스는 <b>우리와 무관한 이유로도</b> 오른다.</item>
/// </list>
/// <para>
/// 그래서 <b>세대는 이 타입이 직접 센다</b> — 구성원 집합이 <b>실제로 달라졌을 때만</b>
/// 하나 오른다. Consul 인덱스는 다음 블로킹 쿼리의 인자로만 쓰인다.
/// </para>
///
/// <para>
/// <b>⚠ 블로킹 쿼리는 내용이 같은데도 깨어난다.</b> 서비스의 다른 필드(체크 출력·태그)가
/// 바뀌거나 서버가 일찍 응답하면 인덱스만 오른다. 그때 세대를 올리면 <b>모든 노드가
/// 헛되이 소유권을 재검토</b>하고(ADR-0052 의 <c>WatchAsync</c> 소비자들), 그 비용은
/// 노드 수만큼 곱해진다. 그래서 <b>내용을 비교해서</b> 올린다.
/// </para>
///
/// <para>
/// <b>⚠⚠ 기동 시 첫 조회에 실패하면 만들어지지 않는다.</b> 구성을 모르는 채로 기동하면
/// 이 노드는 <b>자기 혼자인 뷰</b>를 들고 <b>전 키스페이스를 자기 것이라 믿는다</b> —
/// 그 상태로 트래픽을 받는 것은 기동 실패보다 나쁘다. 그래서
/// <see cref="CreateAsync"/> 가 첫 조회를 마친 뒤에만 인스턴스를 준다.
/// </para>
///
/// <para>
/// <b>⚠ 자기 자신을 뷰에 <i>끼워 넣지</i> 않는다.</b> Consul 이 우리를 건강하지 않다고
/// 보면 우리는 구성원이 아니다 — "<see cref="ClusterView"/> 에 있다 = 지금 보낼 수 있다"
/// 가 이 축의 규칙이고(ADR-0047), 자기만 예외로 두면 <b>남들은 아니라는데 나만 내 것이라
/// 믿는 키</b>가 생긴다. <see cref="Self"/> 는 "나는 누구인가" 이지 "나는 구성원인가" 가 아니다.
/// </para>
///
/// <para>
/// <b>스레드 규약.</b> 모든 멤버가 스레드 안전하다. 배경 루프 하나가 뷰를 교체하고
/// (불변 참조 교체) 여러 소비자가 동시에 읽고 기다린다.
/// </para>
/// </remarks>
public sealed class ConsulClusterMembership : IClusterMembership
{
    private static readonly EventId ViewChangedEvent = new(2020, "ClusterViewChanged");
    private static readonly EventId QueryFailedEvent = new(2021, "ConsulQueryFailed");
    private static readonly EventId MalformedNodeEvent = new(2022, "ConsulMalformedNode");
    private static readonly EventId SelfMissingEvent = new(2023, "SelfNotInView");

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ConsulClusterMembershipOptions _options;
    private readonly IServerLogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _gate = new();

    private ClusterView _view;
    private TaskCompletionSource<ClusterView> _changed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _loop;
    private int _disposed;

    private ConsulClusterMembership(
        HttpClient http,
        bool ownsHttp,
        ConsulClusterMembershipOptions options,
        IServerLogger logger,
        ClusterNode self,
        ClusterView initial)
    {
        _http = http;
        _ownsHttp = ownsHttp;
        _options = options;
        _logger = logger;
        Self = self;
        _view = initial;
    }

    /// <summary>첫 조회를 마친 뒤 인스턴스를 만든다.</summary>
    /// <param name="options">주소·서비스 이름·자기 정체.</param>
    /// <param name="logger">로거.</param>
    /// <param name="httpClient">
    /// 쓸 <see cref="HttpClient"/>. <see langword="null"/> 이면 직접 만들고 <b>직접 정리한다</b>.
    /// 넘기면 <b>호출자가 소유</b>한다.
    /// </param>
    /// <param name="cancellationToken">첫 조회의 취소 토큰.</param>
    /// <returns>구성원을 따라가기 시작한 멤버십.</returns>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/> 이다.</exception>
    /// <exception cref="InvalidOperationException">설정이 성립하지 않거나 첫 조회가 실패했다.</exception>
    /// <remarks>
    /// <b>첫 조회가 실패하면 던진다.</b> 구성을 모르는 채로 기동하는 것보다 낫다(타입 문서).
    /// 이후의 실패는 던지지 않는다 — <b>마지막으로 알던 구성을 유지</b>하고 재시도한다.
    /// </remarks>
    public static async ValueTask<ConsulClusterMembership> CreateAsync(
        ConsulClusterMembershipOptions options,
        IServerLogger logger,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        options.Validate();

        HttpClient http = httpClient ?? new HttpClient();
        bool ownsHttp = httpClient is null;

        try
        {
            (ClusterView view, ulong index) = await QueryAsync(
                http, options, logger, generation: 1, consulIndex: 0, blocking: false, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Consul 첫 조회가 실패했다. 구성을 모르는 채로 기동하면 이 노드는 "
                    + "전 키스페이스를 자기 것이라 믿는다 — 기동을 멈추는 것이 낫다.");

            ClusterNode self = new(options.SelfId, options.SelfName, options.SelfEndPoint);

            ConsulClusterMembership membership = new(http, ownsHttp, options, logger, self, view);
            membership.WarnIfSelfMissing(view);
            membership._loop = Task.Run(() => membership.RunAsync(index), CancellationToken.None);

            return membership;
        }
        catch
        {
            if (ownsHttp)
            {
                http.Dispose();
            }

            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>설정에서 온다.</b> <see cref="Current"/> 에 없을 수도 있다 — 그때 이 노드는
    /// "누구인지는 알지만 지금은 구성원이 아니다" 이며, 그것이 정확한 상태다(타입 문서).
    /// </remarks>
    public ClusterNode Self { get; }

    /// <inheritdoc/>
    public ClusterView Current => Volatile.Read(ref _view);

    /// <inheritdoc/>
    public ValueTask<ClusterView> WaitForChangeAsync(int knownGeneration, CancellationToken cancellationToken)
    {
        Task<ClusterView> pending;

        lock (_gate)
        {
            // ⚠ 세대를 잠금 안에서 본다 — "확인 직후·대기 직전" 창을 닫는 것이 이 인자의 목적이고,
            //   밖에서 보면 그 창이 그대로 열린다.
            if (knownGeneration < _view.Generation)
            {
                return new ValueTask<ClusterView>(_view);
            }

            pending = _changed.Task;
        }

        return new ValueTask<ClusterView>(pending.WaitAsync(cancellationToken));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            // ⚠ 관측만 하고 기다리기에 상한을 둔다. 배경 루프가 HTTP 응답을 기다리는 중이면
            //   취소가 즉시 먹지 않을 수 있고, 그때 무한 대기면 종료가 볼모로 잡힌다
            //   (ADR-0051 에서 읽기 루프를 await 했다가 무한 정지를 재현한 것과 같은 자리).
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 정상 종료 경로다.
            }
            catch (TimeoutException)
            {
                // 상한을 넘겼다. 아래에서 자원을 정리하고 진행한다.
            }
        }

        lock (_gate)
        {
            _changed.TrySetCanceled();
        }

        _shutdown.Dispose();

        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    /// <summary>블로킹 쿼리를 이어 돌며 뷰를 갱신한다.</summary>
    private async Task RunAsync(ulong index)
    {
        CancellationToken token = _shutdown.Token;

        while (!token.IsCancellationRequested)
        {
            (ClusterView View, ulong Index)? result;

            try
            {
                result = await QueryAsync(
                    _http, _options, _logger, NextGeneration(), index, blocking: true, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }

            if (result is null)
            {
                // 조회가 실패했다. **마지막으로 알던 구성을 유지**하고 쉬었다 다시 본다 —
                // Consul 이 잠깐 없다고 해서 클러스터가 사라진 것이 아니다.
                try
                {
                    await Task.Delay(_options.RetryDelay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            (ClusterView view, ulong newIndex) = result.Value;

            // ⚠ Consul 인덱스가 되돌아가면 0 으로 리셋한다(Consul 문서의 권고). 그대로 쓰면
            //   블로킹 쿼리가 영원히 즉시 반환하거나 영원히 멈춘다.
            index = newIndex < index ? 0 : newIndex;

            Publish(view);
        }
    }

    /// <summary>구성원이 <b>실제로</b> 달라졌을 때만 뷰를 교체하고 기다리는 쪽을 깨운다.</summary>
    private void Publish(ClusterView candidate)
    {
        TaskCompletionSource<ClusterView>? waiters = null;

        lock (_gate)
        {
            // 고의 회귀로 확인: 이 비교를 없애면 무관한 변경 3회에 세대가 1→4 로 올랐다.
            if (SameMembers(_view, candidate))
            {
                // 인덱스만 올랐다. 세대를 올리면 모든 노드가 헛되이 재검토한다.
                return;
            }

            Volatile.Write(ref _view, candidate);
            waiters = _changed;
            _changed = new TaskCompletionSource<ClusterView>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        WarnIfSelfMissing(candidate);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.Log(
                LogLevel.Information, ViewChangedEvent, (candidate.Generation, candidate.Count), null,
                static (state, _) => $"클러스터 구성 변경: 세대 {state.Generation}, 노드 {state.Count}개");
        }

        waiters.TrySetResult(candidate);
    }

    /// <summary>다음 뷰에 붙일 세대. <b>Consul 인덱스가 아니라 우리 카운터다</b>(타입 문서).</summary>
    private int NextGeneration()
    {
        // 잠금 없이 읽어도 된다 — 이 값을 쓰는 것은 배경 루프 하나뿐이고,
        // 실제 교체는 Publish 가 잠금 안에서 다시 판정한다.
        int current = Volatile.Read(ref _view).Generation;
        return current == int.MaxValue ? current : current + 1;
    }

    /// <summary>노드 집합이 같은가 — 번호·이름·주소를 모두 본다.</summary>
    /// <remarks>
    /// <b>번호만 비교하면 주소 변경을 놓친다.</b> 그러면 노드가 이사한 뒤에도 옛 주소로
    /// 계속 보내고, 증상은 "그 노드만 조용히 안 받는다" 로 나타난다.
    /// 뷰는 식별자 사전 순으로 고정돼 있으므로(ADR-0047) 순서대로 비교하면 된다.
    /// </remarks>
    private static bool SameMembers(ClusterView left, ClusterView right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            ClusterNode a = left.Nodes[i];
            ClusterNode b = right.Nodes[i];

            if (a.Id != b.Id
                || !string.Equals(a.Name, b.Name, StringComparison.Ordinal)
                || !a.EndPoint.Equals(b.EndPoint))
            {
                return false;
            }
        }

        return true;
    }

    private void WarnIfSelfMissing(ClusterView view)
    {
        if (view.Contains(Self.Id) || !_logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        // ⚠ 조용히 지나가면 안 되는 상태다 — 남들은 나에게 보내지 않고 나도 내 키를
        //   갖지 않는다. 등록이 아직 건강하지 않거나 메타 키를 빠뜨린 것이다.
        _logger.Log(
            LogLevel.Warning, SelfMissingEvent, Self.Id, null,
            static (id, _) =>
                $"이 노드({id.Value})가 Consul 구성원에 없다. 등록이 건강한지, "
                + "노드 번호 메타 키가 맞는지 확인한다.");
    }

    /// <summary>한 번 조회한다. 실패는 <see langword="null"/> 로 돌려준다.</summary>
    /// <remarks>
    /// <b>예외를 흘리지 않는다.</b> 이 경로의 실패는 예외적 상황이 아니라 <b>정상적으로
    /// 일어나는 일</b>이다(에이전트 재시작·네트워크 흔들림). 호출자는 마지막 구성을
    /// 유지하고 재시도하면 되므로 제어 흐름에 예외를 쓰지 않는다(CLAUDE.md 8절).
    /// </remarks>
    private static async Task<(ClusterView View, ulong Index)?> QueryAsync(
        HttpClient http,
        ConsulClusterMembershipOptions options,
        IServerLogger logger,
        int generation,
        ulong consulIndex,
        bool blocking,
        CancellationToken cancellationToken)
    {
        Uri uri = BuildUri(options, consulIndex, blocking);

        try
        {
            using HttpResponseMessage response = await http
                .GetAsync(uri, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogQueryFailure(logger, $"HTTP {(int)response.StatusCode}");
                return null;
            }

            ulong index = ReadIndex(response, consulIndex);

            ConsulHealthEntry[]? entries = await JsonSerializer
                .DeserializeAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    ConsulJsonContext.Default.ConsulHealthEntryArray,
                    cancellationToken)
                .ConfigureAwait(false);

            return (BuildView(entries, options, logger, generation), index);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            LogQueryFailure(logger, ex.Message);
            return null;
        }
        catch (TaskCanceledException ex)
        {
            // 블로킹 쿼리의 HttpClient 타임아웃. 취소가 아니라 실패로 다룬다.
            LogQueryFailure(logger, ex.Message);
            return null;
        }
        catch (JsonException ex)
        {
            LogQueryFailure(logger, $"응답을 해석할 수 없다: {ex.Message}");
            return null;
        }
    }

    private static Uri BuildUri(ConsulClusterMembershipOptions options, ulong consulIndex, bool blocking)
    {
        // passing=true — 건강한 인스턴스만. "살아 있는가" 는 제공자가 답한다(ADR-0047).
        string query = $"/v1/health/service/{Uri.EscapeDataString(options.ServiceName)}?passing=true";

        if (blocking)
        {
            query +=
                $"&index={consulIndex.ToString(CultureInfo.InvariantCulture)}"
                + $"&wait={((int)options.WaitTime.TotalSeconds).ToString(CultureInfo.InvariantCulture)}s";
        }

        return new Uri(options.Address, query);
    }

    private static ulong ReadIndex(HttpResponseMessage response, ulong fallback)
    {
        if (response.Headers.TryGetValues("X-Consul-Index", out IEnumerable<string>? values))
        {
            foreach (string value in values)
            {
                if (ulong.TryParse(value, CultureInfo.InvariantCulture, out ulong index))
                {
                    // ⚠ 인덱스 0 으로 블로킹하면 Consul 이 즉시 반환한다 — 바쁜 대기가 된다.
                    return index == 0 ? 1 : index;
                }
            }
        }

        return fallback;
    }

    /// <summary>응답을 뷰로 만든다. <b>해석할 수 없는 노드는 버리고 기록한다</b>.</summary>
    private static ClusterView BuildView(
        ConsulHealthEntry[]? entries,
        ConsulClusterMembershipOptions options,
        IServerLogger logger,
        int generation)
    {
        List<ClusterNode> nodes = [];

        foreach (ConsulHealthEntry entry in entries ?? [])
        {
            ConsulService? service = entry.Service;
            if (service?.Meta is null)
            {
                continue;
            }

            if (!service.Meta.TryGetValue(options.NodeIdMetaKey, out string? rawId)
                || !ushort.TryParse(rawId, CultureInfo.InvariantCulture, out ushort id))
            {
                // ⚠ 짐작하지 않는다. 번호를 모르면 구성원이 아니다 — 잘못된 번호로
                //   라우팅하면 ObjectId 가 조용히 충돌한다(ADR-0048).
                LogMalformed(logger, service.Id, $"'{options.NodeIdMetaKey}' 메타가 없거나 숫자가 아니다");
                continue;
            }

            if (string.IsNullOrWhiteSpace(service.Address))
            {
                LogMalformed(logger, service.Id, "주소가 비어 있다");
                continue;
            }

            int port = ResolvePeerPort(service, options);
            if (port is <= 0 or > 65535)
            {
                LogMalformed(logger, service.Id, $"노드 간 포트가 범위를 벗어났다: {port}");
                continue;
            }

            nodes.Add(new ClusterNode(new NodeId(id), service.Id ?? $"node-{id}", new DnsEndPoint(service.Address, port)));
        }

        // 중복 번호·이름은 ClusterView 가 판정한다 — 규칙의 정본이 한 곳이어야 한다(ADR-0048).
        return new ClusterView(nodes, generation);
    }

    /// <summary>노드 간 포트를 정한다 — 메타가 있으면 그것, 없으면 서비스 포트.</summary>
    private static int ResolvePeerPort(ConsulService service, ConsulClusterMembershipOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PeerPortMetaKey)
            && service.Meta!.TryGetValue(options.PeerPortMetaKey, out string? rawPort)
            && int.TryParse(rawPort, CultureInfo.InvariantCulture, out int peerPort))
        {
            return peerPort;
        }

        return service.Port;
    }

    private static void LogQueryFailure(IServerLogger logger, string reason)
    {
        if (logger.IsEnabled(LogLevel.Warning))
        {
            logger.Log(
                LogLevel.Warning, QueryFailedEvent, reason, null,
                static (state, _) => $"Consul 조회 실패, 마지막 구성을 유지한다: {state}");
        }
    }

    private static void LogMalformed(IServerLogger logger, string? serviceId, string reason)
    {
        if (logger.IsEnabled(LogLevel.Warning))
        {
            logger.Log(
                LogLevel.Warning, MalformedNodeEvent, (serviceId, reason), null,
                static (state, _) => $"구성원에서 제외한 등록 '{state.serviceId ?? "?"}': {state.reason}");
        }
    }
}
