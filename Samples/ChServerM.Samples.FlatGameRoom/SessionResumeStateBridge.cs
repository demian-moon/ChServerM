using System;
using System.Threading.Tasks;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Identity;

namespace ChServerM.Samples.FlatGameRoom;

/// <summary>
/// 세션 재개 성공을 앱의 상태 전이(로그인 완료)로 잇는 미들웨어.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 프레임워크의 재개 처리(<c>SessionResumeDispatch</c>)는 <b>세션 바인딩</b>
/// (<see cref="ISessionFeature"/>)까지만 세운다 — "재개한 커넥션에 어떤 메시지를 허용할
/// 것인가"는 상태 비트의 의미와 마찬가지로 앱 정책이기 때문이다(ADR-0004). 이 미들웨어가
/// 없으면 재개에 성공한 커넥션도 상태 필터 기준으로는 여전히 '연결 직후'라서, 클라이언트가
/// 재로그인을 해야 한다 — 그러면 재개가 상태를 복구해 주는 의미가 없다.
/// </para>
/// <para>
/// <b>동작.</b> 재개 프레임(40007)의 디스패치를 감싸, 프레임워크 처리(<c>next</c>)가 끝난 뒤
/// 세션이 바인딩됐으면 (1) 세션 상태에서 플레이어 신원을 복원하고
/// (<see cref="FlatGameRoomService.TryRestoreAfterResumeAsync"/>),
/// (2) <see cref="IConnectionStateFeature"/> 를 <see cref="ConnectionStates.LoggedIn"/> 으로
/// 전이한다 — <c>AuthenticationMiddleware</c> 의 성공 경로와 같은 대체 전이다.
/// 거부된 재개는 세션이 바인딩되지 않으므로 아무 전이도 일어나지 않는다.
/// </para>
/// <para>
/// <b>조립 규약.</b> 상태 필터·인증 미들웨어 <b>뒤에</b> 등록한다. 필터가 초기 상태에서
/// 40007 을 허용해야 이 미들웨어까지 도달한다는 점에 주의 —
/// <c>MessageStateFilterOptions.Allow(FrameworkMessageIds.SessionResume, …)</c> 를 빠뜨리면
/// 재개 요청이 기본 거부에 걸려 커넥션이 닫힌다(감사 H-7 에서 확인된 상호작용).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 인스턴스는 불변이라 모든 커넥션이 공유한다. 상태·세션 feature 접근은
/// 커넥션 디스패치 순차 컨텍스트 안이다 — 클라이언트가 40008 응답을 받고 보낸 다음 프레임은
/// 이 미들웨어의 사후 처리가 끝난 뒤에야 디스패치되므로(같은 커넥션 = 같은 파티션 = 순차),
/// "응답은 받았는데 상태 전이는 아직" 인 경합 창이 없다.
/// </para>
/// </remarks>
internal sealed class SessionResumeStateBridge(FlatGameRoomService service) : IServerMiddleware
{
    /// <inheritdoc/>
    public async ValueTask<DispatchStatus> InvokeAsync(MessageContext context, MessageDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (context.Envelope.MessageId.Value != FrameworkMessageIds.SessionResume.Value)
        {
            return await next(context).ConfigureAwait(false);
        }

        // 프레임워크의 재개 처리 먼저 — 토큰 대조·회전·응답(40008)·세션 바인딩까지.
        DispatchStatus status = await next(context).ConfigureAwait(false);

        // 성공·거부 모두 Handled 로 끝난다(응답 없이 끊지 않는 규약) — 성공 여부는
        // 상태 코드가 아니라 세션 바인딩의 존재로 판정한다. 거부는 바인딩을 만들지 않고,
        // 이미 로그인/재개된 커넥션의 40007 은 상태 필터가 앞에서 거부하므로(Connected 전용)
        // 낡은 바인딩을 성공으로 오인할 경로가 없다.
        if (status == DispatchStatus.Handled
            && await service.TryRestoreAfterResumeAsync(context).ConfigureAwait(false))
        {
            IConnectionStateFeature? state = context.Connection.Features.Get<IConnectionStateFeature>();
            if (state is not null)
            {
                // 인증 성공과 같은 대체 전이 — 이제 룸·채팅·이동 메시지가 허용된다.
                state.States = ConnectionStates.LoggedIn;
            }
        }

        return status;
    }
}
