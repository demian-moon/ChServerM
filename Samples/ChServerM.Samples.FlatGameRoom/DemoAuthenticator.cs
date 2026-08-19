using System;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Dispatch;
using ChServerM.Samples.FlatGameRoom.Messages;
using ChServerM.Security;
using ChServerM.Serialization.FlatBuffers;

namespace ChServerM.Samples.FlatGameRoom;

/// <summary>
/// 데모용 인증기 — 표시 이름 + 공유 비밀 토큰을 검증한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <see cref="IAuthenticator"/> 를 앱이 어떻게 구현하는지 보이는 것이
/// 목적이다: 자격의 <b>형식</b>(여기서는 FlatBuffers <c>LoginRequest</c>)과 <b>정책</b>
/// (무엇을 유효한 자격으로 볼 것인가)은 전부 앱 소관이고, 프레임워크가 강제하는 것은
/// "검증 결과를 무시할 수 없는 구조"뿐이다(T-20 — 실패 반환 = 커넥션 종료).
/// </para>
/// <para>
/// <b>⚠ 데모다.</b> 공유 비밀 문자열 비교는 자격 검증이 아니라 자리 표시다. 실서비스라면
/// 이 자리에 PBKDF2 비밀번호 검증(<c>ChServerM.Security.AspNetIdentity</c>)이나 플랫폼
/// 티켓 검증이 온다. 레거시의 실패는 검증 자체가 아니라 <b>검증 결과를 버린 호출부</b>였고
/// (legacy/07-security AuthM #1), 이 구조에서는 그 실수가 성립하지 않는다.
/// </para>
/// <para>
/// <b>수명 규약.</b> 페이로드는 반환 시점에 무효가 된다(<see cref="IAuthenticator"/> 계약).
/// 이 구현은 <c>await</c> 없이 동기로 역직렬화를 끝내고, Greedy 역직렬화가 버퍼를 복사하므로
/// 반환 후 페이로드를 참조하는 경로가 없다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 서로 다른 커넥션이 같은 인스턴스를 동시에 부른다 — 공유 상태는
/// 플레이어 번호 카운터 하나이고 <see cref="Interlocked"/> 로 갱신한다. 커넥션 피처 접근은
/// 그 커넥션의 디스패치 순차 컨텍스트 안이라 안전하다.
/// </para>
/// </remarks>
internal sealed class DemoAuthenticator : IAuthenticator
{
    /// <summary>데모용 공유 비밀. 자체 검증 클라이언트와 서버가 공유한다.</summary>
    public const string SharedSecret = "flat-game-room-demo-token";

    /// <summary>표시 이름 최대 길이. 무제한 문자열을 계정 표시에 쓰지 않는다.</summary>
    private const int MaxDisplayNameLength = 32;

    /// <summary>자격 역직렬화기. 상태가 없으므로 공유해도 안전하다.</summary>
    private static readonly FlatSharpMessageSerializer<LoginRequest> RequestSerializer =
        new(LoginRequest.Serializer);

    /// <summary>플레이어 번호 발급 카운터. 0 은 "미발급" 의미로 남겨 1 부터 나간다.</summary>
    private long _playerSeed;

    /// <inheritdoc/>
    public ValueTask<AuthenticationResult> AuthenticateAsync(MessageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 손상 자격은 예외가 아니라 실패 값이다 — 로그인 폭주가 예외 비용을 증폭시키면
        // 그 자체가 서비스 거부 경로가 된다(T-16).
        if (!RequestSerializer.TryDeserialize(context.Payload, out LoginRequest request))
        {
            return ValueTask.FromResult(AuthenticationResult.Failure("LoginRequest 역직렬화 실패"));
        }

        if (!string.Equals(request.ClientToken, SharedSecret, StringComparison.Ordinal))
        {
            // 사유는 서버 로그까지만 간다 — 와이어로 나가면 계정 열거 통로가 된다(T-20).
            return ValueTask.FromResult(AuthenticationResult.Failure("공유 비밀 토큰 불일치"));
        }

        string? displayName = request.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > MaxDisplayNameLength)
        {
            return ValueTask.FromResult(AuthenticationResult.Failure("표시 이름 형식 위반"));
        }

        // 신원은 커넥션 피처로 남긴다(IAuthenticator 문서의 권장 경로). 로그인 핸들러가
        // 이것을 읽어 세션을 수립하고, 채팅·이동 핸들러가 발신자 표기에 쓴다.
        long playerId = Interlocked.Increment(ref _playerSeed);
        context.Connection.Features.Set(new PlayerFeature(playerId, displayName));

        // 성공 = 상태 대체 전이. 미들웨어가 이 비트를 IConnectionStateFeature 에 쓴다.
        return ValueTask.FromResult(AuthenticationResult.Success(ConnectionStates.LoggedIn));
    }
}

/// <summary>
/// 이 커넥션의 플레이어 신원 — 인증 성공 또는 세션 재개 복원으로 붙는 앱 정의 피처.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> "누가 보냈는가"를 프레임마다 다시 알아낼 수 없다 — 자격은 로그인
/// 프레임에만 실려 오기 때문이다. 커넥션 수명 동안 유지할 값은 <c>IConnection.Features</c>
/// 에 둔다는 프레임워크 규약을 그대로 따른다.
/// </para>
/// <para>
/// <b>수명 규약.</b> 두 경로로 생긴다: (1) <see cref="DemoAuthenticator"/> 성공,
/// (2) 세션 재개 후 <see cref="SessionResumeStateBridge"/> 가 세션 상태에서 복원.
/// 커넥션이 닫히면 커넥션과 함께 사라진다 — 세션에 남는 것은 이 값의 직렬화본이다.
/// </para>
/// <para><b>스레드 규약.</b> 불변이다. 읽기는 디스패치 순차 컨텍스트 안에서만 일어난다.</para>
/// </remarks>
internal sealed class PlayerFeature(long playerId, string displayName)
{
    /// <summary>서버가 발급한 플레이어 식별자.</summary>
    public long PlayerId { get; } = playerId;

    /// <summary>검증을 통과한 표시 이름.</summary>
    public string DisplayName { get; } = displayName;
}
