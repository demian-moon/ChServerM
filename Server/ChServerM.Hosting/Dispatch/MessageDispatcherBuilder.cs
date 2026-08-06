using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Identity;
using ChServerM.Serialization;

namespace ChServerM.Hosting.Dispatch;

/// <summary>
/// 미들웨어와 핸들러를 엮어 <see cref="MessageDispatcher"/>를 만든다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> "조립 비용은 시작 시점에 지불한다"(ADR-0000)를 실행하는 곳이다.
/// 미들웨어 체인을 델리게이트로 미리 접어두고, 라우팅 테이블을 배열로 굳힌다.
/// 핫패스에는 동적 결정이 하나도 남지 않는다.
/// </para>
/// <para>
/// <b>미들웨어 실행 순서는 등록 순서다.</b> 먼저 등록한 것이 바깥쪽에서 돈다
/// (ASP.NET Core 와 같은 멘탈 모델). 그래서 인증을 먼저 등록하면
/// 그 뒤 모든 것이 인증 뒤에 온다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 빌더는 스레드 안전하지 않다. 조립은 단일 스레드로 끝낸다.
/// <see cref="Build"/>가 만든 디스패처는 스레드 안전하다.
/// </para>
/// </remarks>
public sealed class MessageDispatcherBuilder
{
    private static readonly EventId HandlerNotFoundEvent = new(4000, "HandlerNotFound");
    private static readonly EventId DeserializationFailedEvent = new(3000, "DeserializationFailed");

    private readonly Dictionary<ushort, MessageDelegate> _routes = [];
    private readonly List<Func<MessageDelegate, MessageDelegate>> _middleware = [];

    // 인터페이스로 등록된 미들웨어의 인스턴스 목록(등록 순서 유지) — Build 의 조립 검증용.
    // 델리게이트 미들웨어는 타입을 알 수 없으므로 여기 없다.
    private readonly List<IServerMiddleware> _middlewareInstances = [];

    private IServerLogger _logger = NullServerLogger.Instance;

    /// <summary>진단 로거를 지정한다.</summary>
    /// <param name="logger">로거.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    public MessageDispatcherBuilder UseLogger(IServerLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        return this;
    }

    /// <summary>미들웨어를 추가한다.</summary>
    /// <param name="middleware">추가할 미들웨어.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <remarks>먼저 등록한 것이 바깥쪽에서 돈다.</remarks>
    public MessageDispatcherBuilder Use(IServerMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        // 조립 시점에 한 번만 클로저를 만든다. 프레임마다 만들면 그게 곧 할당이다.
        _middleware.Add(next => context => middleware.InvokeAsync(context, next));
        _middlewareInstances.Add(middleware);
        return this;
    }

    /// <summary>델리게이트 형태의 미들웨어를 추가한다.</summary>
    /// <param name="middleware">다음 단계를 받아 새 단계를 만드는 함수.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <remarks>인터페이스를 만들 만큼 무겁지 않은 단계를 위한 지름길이다.</remarks>
    public MessageDispatcherBuilder Use(Func<MessageDelegate, MessageDelegate> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _middleware.Add(middleware);
        return this;
    }

    /// <summary>역직렬화 없이 원시 페이로드를 받는 핸들러를 등록한다.</summary>
    /// <param name="messageId">이 핸들러가 받을 메시지 식별자.</param>
    /// <param name="handler">처리 델리게이트.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentException">같은 식별자가 이미 등록돼 있을 때.</exception>
    /// <remarks>
    /// 에코·프록시·바이트 그대로 전달하는 경로에 쓴다.
    /// 직렬화 축을 아직 고르지 않아도 파이프라인 전체를 검증할 수 있다.
    /// </remarks>
    public MessageDispatcherBuilder MapRaw(MessageId messageId, MessageDelegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        AddRoute(messageId, handler);
        return this;
    }

    /// <summary>역직렬화까지 해주는 타입 있는 핸들러를 등록한다.</summary>
    /// <typeparam name="TMessage">메시지 타입.</typeparam>
    /// <param name="messageId">이 핸들러가 받을 메시지 식별자.</param>
    /// <param name="serializer">페이로드 역직렬화기.</param>
    /// <param name="handler">처리기.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentException">같은 식별자가 이미 등록돼 있을 때.</exception>
    /// <remarks>
    /// <b>직렬화기를 여기서 한 번만 찾는다.</b> 프레임마다 조회하면 그 비용이 핫패스에 들어온다.
    /// </remarks>
    public MessageDispatcherBuilder Map<TMessage>(
        MessageId messageId,
        IMessageSerializer<TMessage> serializer,
        IMessageHandler<TMessage> handler)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(handler);

        IServerLogger logger = _logger;

        AddRoute(messageId, async context =>
        {
            if (!serializer.TryDeserialize(context.Payload, out TMessage? message))
            {
                // 손상된 페이로드는 정상적인 입력의 일부다(버그이거나 공격이다).
                // 예외로 처리하면 그 자체가 서비스 거부 경로가 된다.
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.Log(
                        LogLevel.Warning,
                        DeserializationFailedEvent,
                        messageId.Value,
                        null,
                        static (id, _) => $"메시지 {id} 페이로드를 역직렬화할 수 없다.");
                }

                return DispatchStatus.DeserializationFailed;
            }

            await handler.HandleAsync(context, message).ConfigureAwait(false);
            return DispatchStatus.Handled;
        });

        return this;
    }

    /// <summary>등록된 내용으로 디스패처를 만든다.</summary>
    /// <returns>조립이 끝난 디스패처.</returns>
    /// <remarks>
    /// <para>
    /// 라우팅 테이블은 <b>등록된 최대 식별자 크기의 배열</b>이다. 앱 대역(1~40000)만 쓰면
    /// 배열도 그만큼만 잡힌다. 프레임워크 대역(40001~)을 등록하면 그 크기까지 늘어난다 —
    /// 델리게이트 참조 배열이므로 x64 기준 식별자당 8바이트다.
    /// </para>
    /// <para>여러 번 호출해도 되지만, 매번 새 배열을 만든다.</para>
    /// </remarks>
    public MessageDispatcher Build()
    {
        EnsureKnownMiddlewareOrder();

        MessageDelegate[] routes = BuildRoutingTable(out MessageDelegate notFound);
        MessageDelegate pipeline = BuildRoutingTerminal(routes, notFound);

        // 뒤에서부터 감싼다 — 먼저 등록한 미들웨어가 가장 바깥이 된다.
        for (int i = _middleware.Count - 1; i >= 0; i--)
        {
            pipeline = _middleware[i](pipeline)
                ?? throw new InvalidOperationException($"{i}번째 미들웨어가 null 을 반환했다.");
        }

        return new MessageDispatcher(pipeline, _logger);
    }

    /// <summary>프레임워크 미들웨어 사이의 순서 모순을 조립 시점에 잡는다.</summary>
    /// <exception cref="InvalidOperationException">
    /// 필터(<see cref="MessageStateFilterMiddleware"/>) → 인증(<see cref="AuthenticationMiddleware"/>)
    /// → 인가(<see cref="AuthorizationMiddleware"/>) 순서가 뒤집혀 등록됐을 때.
    /// </exception>
    /// <remarks>
    /// <para>정당한 순서는 <b>필터 → 인증 → 인가</b>다(존재하는 것끼리만 비교한다 —
    /// 일부만 조립하는 워크로드가 정당하다, ADR-0004).</para>
    /// <list type="bullet">
    ///   <item><description>인증이 필터보다 바깥: 인증 성공 직후 전이된 상태에서 필터가
    ///   그 자격 메시지를 검사한다 — 전이 후 화이트리스트에 자격 메시지가 없으면(재로그인
    ///   차단의 정석 구성) <b>인증에 성공한 커넥션이 곧바로 닫히는</b> 런타임 미스터리가 된다</description></item>
    ///   <item><description>인가가 인증보다 바깥: 인가 정책이 읽는 신원 피처가 아직 등록되지
    ///   않아 자격 메시지의 인가가 신원 없이 판정된다</description></item>
    ///   <item><description>인가가 필터보다 바깥: 화이트리스트 밖 메시지가 정책을 먼저
    ///   두드린다 — 기본 거부 경계가 흐려진다</description></item>
    /// </list>
    /// <para>죽은 조립은 조립 시점 예외가 옳다.</para>
    /// </remarks>
    private void EnsureKnownMiddlewareOrder()
    {
        int stateFilterIndex = -1;
        int authenticationIndex = -1;
        int authorizationIndex = -1;

        for (int i = 0; i < _middlewareInstances.Count; i++)
        {
            if (stateFilterIndex < 0 && _middlewareInstances[i] is MessageStateFilterMiddleware)
            {
                stateFilterIndex = i;
            }

            if (authenticationIndex < 0 && _middlewareInstances[i] is AuthenticationMiddleware)
            {
                authenticationIndex = i;
            }

            if (authorizationIndex < 0 && _middlewareInstances[i] is AuthorizationMiddleware)
            {
                authorizationIndex = i;
            }
        }

        ThrowIfOutOfOrder(
            stateFilterIndex, authenticationIndex,
            nameof(MessageStateFilterMiddleware), nameof(AuthenticationMiddleware),
            "순서가 뒤집히면 인증 성공 직후 전이된 상태에서 필터가 자격 메시지를 거부해 커넥션이 닫힌다.");

        ThrowIfOutOfOrder(
            authenticationIndex, authorizationIndex,
            nameof(AuthenticationMiddleware), nameof(AuthorizationMiddleware),
            "인가 정책은 인증기가 등록한 신원 피처를 읽는다 — 인증 앞에서는 신원 없이 판정된다.");

        ThrowIfOutOfOrder(
            stateFilterIndex, authorizationIndex,
            nameof(MessageStateFilterMiddleware), nameof(AuthorizationMiddleware),
            "화이트리스트 밖 메시지가 정책을 먼저 두드린다 — 기본 거부 경계가 흐려진다.");
    }

    /// <summary>둘 다 등록됐는데 앞뒤가 바뀌었으면 조립 예외를 던진다.</summary>
    private static void ThrowIfOutOfOrder(int outerIndex, int innerIndex, string outer, string inner, string why)
    {
        if (outerIndex >= 0 && innerIndex >= 0 && innerIndex < outerIndex)
        {
            throw new InvalidOperationException(
                $"{inner} 가 {outer} 보다 먼저(바깥에) 등록됐다. {outer} 를 먼저 등록한다 — {why}");
        }
    }

    private void AddRoute(MessageId messageId, MessageDelegate handler)
    {
        if (messageId.IsNone)
        {
            throw new ArgumentException(
                "메시지 식별자 0 은 '설정되지 않음'을 뜻하는 센티넬이다. 핸들러를 붙일 수 없다.",
                nameof(messageId));
        }

        if (!_routes.TryAdd(messageId.Value, handler))
        {
            // 중복 등록을 덮어쓰면 어느 핸들러가 도는지 알 수 없게 된다.
            // 조립 시점 실패이므로 예외가 옳다.
            throw new ArgumentException(
                $"메시지 식별자 {messageId.Value} 에 이미 핸들러가 등록돼 있다.",
                nameof(messageId));
        }
    }

    private MessageDelegate[] BuildRoutingTable(out MessageDelegate notFound)
    {
        IServerLogger logger = _logger;
        MessageDelegate notFoundRoute = context =>
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.Log(
                    LogLevel.Warning,
                    HandlerNotFoundEvent,
                    context.Envelope.MessageId.Value,
                    null,
                    static (id, _) => $"메시지 {id} 에 등록된 핸들러가 없다.");
            }

            return ValueTask.FromResult(DispatchStatus.HandlerNotFound);
        };

        notFound = notFoundRoute;

        int size = 0;
        foreach (ushort id in _routes.Keys)
        {
            size = Math.Max(size, id + 1);
        }

        MessageDelegate[] routes = new MessageDelegate[size];
        Array.Fill(routes, notFoundRoute);

        foreach (KeyValuePair<ushort, MessageDelegate> entry in _routes)
        {
            routes[entry.Key] = entry.Value;
        }

        return routes;
    }

    private static MessageDelegate BuildRoutingTerminal(MessageDelegate[] routes, MessageDelegate notFound)
    {
        // 배열 인덱싱 하나. 범위 밖이면 등록되지 않은 식별자다.
        return context =>
        {
            ushort id = context.Envelope.MessageId.Value;
            return id < routes.Length ? routes[id](context) : notFound(context);
        };
    }
}
