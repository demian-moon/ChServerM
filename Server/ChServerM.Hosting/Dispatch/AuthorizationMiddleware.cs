using System;
using System.Collections.Frozen;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Identity;
using ChServerM.Security;

namespace ChServerM.Hosting.Dispatch;

/// <summary>
/// 보호 대상 메시지를 <see cref="IAuthorizationPolicy"/> 로 판정하는 미들웨어 (THREAT-MODEL T-21).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 상태 비트로 표현할 수 없는 자원 수준 인가("자기 소유 오브젝트만
/// 수정")를 핸들러 도달 <b>전</b>에 판정한다. 메시지 수준의 거친 인가는 여기가 아니라
/// 상태 화이트리스트(T-19) + 인증 <c>GrantedStates</c> 의 몫이다 —
/// <see cref="AuthorizationOptions"/> 문서 참조.
/// </para>
/// <para>
/// <b>거부 처리는 인증과 의도적으로 다르다.</b> 인증 실패는 옵션 무관 무조건 종료지만
/// (<see cref="AuthenticationMiddleware"/>, T-20), 인가 거부는 <b>정당한 세션의 정상
/// 흐름</b>일 수 있다(권한 밖 버튼 클릭). 그래서 기존
/// <see cref="DispatchStatus.RejectedByPolicy"/>(6001)를 반환하고, 종료 여부는
/// <c>FramedConnectionOptions.CloseOnPolicyRejection</c>(기본 유지)이 정한다.
/// 거부는 항상 경고 로그로 관측된다(T-07 — 닫지 않아도 조용하지 않다).
/// </para>
/// <para>
/// <b>조립 규약.</b> 필터(T-19) → 인증(T-20) → <b>인가</b> 순서다 — 인가는 인증기가
/// 등록한 신원 피처를 읽으므로 인증 뒤여야 한다. 순서 위반은
/// <c>MessageDispatcherBuilder.Build()</c> 가 조립 시점 예외로 거부한다.
/// </para>
/// <para>
/// <b>성능.</b> 보호 목록을 <see cref="FrozenSet{T}"/> 으로 동결 — 비보호 메시지는
/// 프레임당 조회 1회로 통과하고 정책 호출이 없다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 인스턴스는 불변이라 모든 커넥션이 공유한다. 정책 호출은 커넥션
/// 디스패치 순차 컨텍스트 안이다.
/// </para>
/// </remarks>
public sealed class AuthorizationMiddleware : IServerMiddleware
{
    private static readonly EventId AuthorizationDeniedEvent = new(6001, "AuthorizationDenied");

    private readonly FrozenSet<ushort> _protectedMessages;
    private readonly IAuthorizationPolicy _policy;
    private readonly IServerLogger _logger;

    /// <summary>설정을 검증·동결해 미들웨어를 만든다.</summary>
    /// <param name="options">보호 대상 목록. 생성 이후의 옵션 변경은 반영되지 않는다.</param>
    /// <param name="policy">인가 정책 구현.</param>
    /// <param name="logger">진단 로거. 생략하면 기록하지 않는다.</param>
    /// <exception cref="ArgumentNullException">필수 인자가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    public AuthorizationMiddleware(
        AuthorizationOptions options,
        IAuthorizationPolicy policy,
        IServerLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        options.Validate();

        _protectedMessages = options.ProtectedMessages.ToFrozenSet();
        _policy = policy;
        _logger = logger ?? NullServerLogger.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<DispatchStatus> InvokeAsync(MessageContext context, MessageDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!_protectedMessages.Contains(context.Envelope.MessageId.Value))
        {
            // 보호 대상이 아니다 — 메시지 수준 차단은 상태 필터(T-19)의 몫이다.
            return await next(context).ConfigureAwait(false);
        }

        AuthorizationDecision decision = await _policy.AuthorizeAsync(context).ConfigureAwait(false);

        if (!decision.IsAllowed)
        {
            LogDenied(context.Connection.Id, context.Envelope.MessageId, decision.DenyDescription);
            // 종료 여부는 CloseOnPolicyRejection 이 정한다 — 인증(무조건 종료)과 의도적 비대칭.
            return DispatchStatus.RejectedByPolicy;
        }

        return await next(context).ConfigureAwait(false);
    }

    private void LogDenied(ConnectionId id, MessageId messageId, string? description)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                AuthorizationDeniedEvent,
                (id, messageId: messageId.Value, description),
                null,
                static (state, _) =>
                    $"커넥션 {state.id} 메시지 {state.messageId} 인가 거부: {state.description ?? "(사유 없음)"}");
        }
    }
}
