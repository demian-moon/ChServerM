using System;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Identity;
using ChServerM.Security;

namespace ChServerM.Hosting.Dispatch;

/// <summary>
/// 자격 메시지를 <see cref="IAuthenticator"/> 로 검증하고, 성공을 상태 전이로 바꾸는
/// 미들웨어 (THREAT-MODEL T-20).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 검증 결과를 무시할 수 없는 구조.</b> 레거시는 올바른 PBKDF2 검증을
/// 해놓고 호출부가 <c>WRONG_PW</c> 의 <c>return</c> 을 주석 처리해 전부 무의미해졌다
/// (legacy/07-security AuthM #1). 이 미들웨어는 실패 시 <c>next</c> 를 호출하지 않고
/// <see cref="DispatchStatus.RejectedByAuthentication"/> 을 반환한다 — 읽기 루프가
/// <b>옵션 무관하게</b> <c>ErrorCode.AuthenticationFailed</c>(6000)로 커넥션을 닫으므로,
/// "검증은 했는데 결과를 버리는" 코드가 성립할 자리가 없다.
/// </para>
/// <para>
/// <b>성공 = 상태 대체 전이.</b> <c>AuthenticationResult.GrantedStates</c> 를
/// <see cref="IConnectionStateFeature"/> 에 대체(replace)로 쓴다. "인증됐다" 플래그를
/// 따로 두지 않는다 — 인증 여부와 허용 메시지 집합이 어긋날 표면을 없앤다.
/// 전이 후 <c>next</c> 를 호출하므로 앱의 자격 메시지 핸들러가 성공 응답·후속 작업을
/// 담당한다.
/// </para>
/// <para>
/// <b>조립 규약.</b> <see cref="MessageStateFilterMiddleware"/>(T-19)와 함께 쓸 때는
/// 필터를 먼저 등록한다(필터가 바깥) — 순서가 뒤집히면
/// <c>MessageDispatcherBuilder.Build()</c> 가 조립 시점 예외로 거부한다.
/// 초기 상태 화이트리스트에 자격 메시지를 허용해야 로그인이 도달하고,
/// 전이 후 상태에서 자격 메시지를 빼면 재로그인이 차단된다(기존 패턴).
/// </para>
/// <para>
/// <b>실패 사유는 와이어로 나가지 않는다.</b> 계정 존재 여부를 노출하는 통로가 되기
/// 때문에(계정 열거) 서버 로그까지만 간다. 실패한 클라이언트는 종료만 관측한다 —
/// 실패 통지 UX 는 Phase 10 거부 통지 체계와 함께 설계한다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 인스턴스는 불변이라 모든 커넥션이 공유한다. 상태 feature 접근은
/// 커넥션 디스패치 순차 컨텍스트 안이다. 실행 모델(파티션 배타)과 조립하면 인증기의
/// 외부 I/O 동안 같은 파티션의 다른 커넥션도 대기한다 — 느린 인증은 파티션 점유 시간이다.
/// </para>
/// </remarks>
public sealed class AuthenticationMiddleware : IServerMiddleware
{
    private static readonly EventId AuthenticatedEvent = new(6005, "Authenticated");
    private static readonly EventId AuthenticationFailedEvent = new(6000, "AuthenticationFailed");

    private readonly ushort _credentialMessageId;
    private readonly IAuthenticator _authenticator;
    private readonly IServerLogger _logger;

    /// <summary>설정을 검증·복사해 미들웨어를 만든다.</summary>
    /// <param name="options">인증 설정. 생성 이후의 옵션 변경은 반영되지 않는다.</param>
    /// <param name="authenticator">자격 검증 구현.</param>
    /// <param name="logger">진단 로거. 생략하면 기록하지 않는다.</param>
    /// <exception cref="ArgumentNullException">필수 인자가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    public AuthenticationMiddleware(
        AuthenticationOptions options,
        IAuthenticator authenticator,
        IServerLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authenticator);
        options.Validate();

        _credentialMessageId = options.CredentialMessageId.Value;
        _authenticator = authenticator;
        _logger = logger ?? NullServerLogger.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<DispatchStatus> InvokeAsync(MessageContext context, MessageDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (context.Envelope.MessageId.Value != _credentialMessageId)
        {
            // 자격 메시지가 아니다 — 인증 전 차단은 상태 필터(T-19)의 몫이다.
            return await next(context).ConfigureAwait(false);
        }

        AuthenticationResult result = await _authenticator.AuthenticateAsync(context).ConfigureAwait(false);

        if (!result.IsAuthenticated)
        {
            LogFailed(context.Connection.Id, result.FailureDescription);
            // next 를 부르지 않는다 — 읽기 루프가 옵션 무관하게 6000 으로 닫는다(T-20).
            return DispatchStatus.RejectedByAuthentication;
        }

        IFeatureCollection features = context.Connection.Features;
        IConnectionStateFeature? state = features.Get<IConnectionStateFeature>();
        if (state is null)
        {
            // 상태 필터 없이 인증만 조립된 커넥션의 첫 전이 — 여기서 feature 를 만든다.
            state = new ConnectionStateFeature();
            features.Set(state);
        }

        state.States = result.GrantedStates;
        LogAuthenticated(context.Connection.Id, result.GrantedStates);

        // 성공 응답·프로필 로딩 등 후속은 앱의 자격 메시지 핸들러 몫이다.
        return await next(context).ConfigureAwait(false);
    }

    private void LogAuthenticated(ConnectionId id, uint grantedStates)
    {
        // 보안 이벤트는 관측돼야 한다(T-07). 커넥션당 1회라 Information 이어도 핫패스가 아니다.
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.Log(
                LogLevel.Information,
                AuthenticatedEvent,
                (id, grantedStates),
                null,
                static (state, _) => $"커넥션 {state.id} 인증 성공. 상태 전이: 0x{state.grantedStates:X}");
        }
    }

    private void LogFailed(ConnectionId id, string? description)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                AuthenticationFailedEvent,
                (id, description),
                null,
                static (state, _) =>
                    $"커넥션 {state.id} 인증 실패: {state.description ?? "(사유 없음)"}. 커넥션을 닫는다.");
        }
    }

    /// <summary><see cref="IConnectionStateFeature"/>의 기본 구현 — 필터 없이 인증만 조립된 경우용.</summary>
    private sealed class ConnectionStateFeature : IConnectionStateFeature
    {
        public uint States { get; set; }
    }
}
