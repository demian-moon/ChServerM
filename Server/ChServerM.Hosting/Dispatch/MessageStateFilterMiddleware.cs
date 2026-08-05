using System;
using System.Collections.Frozen;
using System.Threading.Tasks;
using ChServerM.Dispatch;
using ChServerM.Features;

namespace ChServerM.Hosting.Dispatch;

/// <summary>
/// 상태별 메시지 화이트리스트 — 현재 커넥션 상태에서 허용되지 않은 메시지를
/// 핸들러에 닿기 전에 거부한다 (THREAT-MODEL T-19).
/// </summary>
/// <remarks>
/// <para>
/// <b>기본 거부.</b> 규칙에 없는 메시지는 어떤 상태에서도 통과하지 못한다.
/// 레거시 <c>AllowedPkState</c>는 기본값이 전부 허용이라 존재하지 않는 세션이
/// 가장 관대한 권한을 가졌다(docs/legacy/06-session-user) — 그 역이다.
/// 프레임워크 대역 메시지(하트비트 등)도 예외가 아니다 — 인증 전에 받을 것은
/// 조립하는 쪽이 명시적으로 <see cref="MessageStateFilterOptions.Allow"/>한다.
/// </para>
/// <para>
/// <b>등록 순서가 보안 경계다.</b> 이 미들웨어는 파이프라인의 가장 바깥
/// (<c>MessageDispatcherBuilder.Use</c> 첫 번째)에 둔다 — 뒤에 두면 앞 단계가
/// 화이트리스트 밖 메시지를 먼저 본다. 라우팅보다 앞이라는 것은 구조가 보장한다
/// (미들웨어는 항상 라우팅 앞 — Phase 2).
/// </para>
/// <para>
/// <b>거부 = 커넥션 종료.</b> <see cref="DispatchStatus.RejectedByState"/>는
/// 읽기 루프가 <c>ErrorCode.MessageNotAllowedInState</c>(4001)로 커넥션을 닫고
/// 경고 로그를 남긴다 — 상태 위반은 클라이언트 버그이거나 공격이며, 어느 쪽도
/// 계속 대화할 이유가 없다. 조용한 거부는 없다(레거시의 주된 병).
/// </para>
/// <para>
/// <b>성능.</b> 조립 시점에 규칙을 <see cref="FrozenDictionary{TKey,TValue}"/>로
/// 굳힌다 — 프레임당 조회 1회 + 비트 AND. 레거시는 프레임마다 O(n) 선형 탐색에
/// 가상 호출이 n번 붙었다(<c>IServerMiddleware</c> 문서). 상태 feature 는 커넥션당
/// 첫 프레임에 1회 할당된다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 인스턴스는 불변이라 모든 커넥션이 공유한다. 상태 feature 접근은
/// 커넥션 디스패치 순차 컨텍스트 안에서만 일어난다(<see cref="IConnectionStateFeature"/>).
/// </para>
/// </remarks>
public sealed class MessageStateFilterMiddleware : IServerMiddleware
{
    private readonly FrozenDictionary<ushort, uint> _rules;
    private readonly uint _initialStates;

    /// <summary>규칙을 검증·고정해 미들웨어를 만든다.</summary>
    /// <param name="options">화이트리스트 규칙. 생성 이후의 옵션 변경은 반영되지 않는다.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/>가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">
    /// 시작 상태가 빈 집합(0)일 때 — 어떤 메시지도 통과하지 못해 상태 전이가 불가능한 죽은 조립이다.
    /// </exception>
    public MessageStateFilterMiddleware(MessageStateFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.InitialStates == 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MessageStateFilterOptions.InitialStates)}가 0(빈 집합)이다. " +
                "첫 메시지부터 전부 거부되어 상태 전이 자체가 불가능하다 — 시작 상태 비트를 하나 이상 켠다.");
        }

        _rules = options.Rules.ToFrozenDictionary();
        _initialStates = options.InitialStates;
    }

    /// <inheritdoc />
    public ValueTask<DispatchStatus> InvokeAsync(MessageContext context, MessageDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        IFeatureCollection features = context.Connection.Features;
        IConnectionStateFeature? state = features.Get<IConnectionStateFeature>();
        if (state is null)
        {
            // 커넥션의 첫 프레임 — 시작 상태를 여기서 부여한다. 존재하지 않는 상태의
            // 기본값을 "가장 제한적"으로 두는 원칙과 달리 시작 상태가 명시값인 이유:
            // 빈 집합은 죽은 조립이라 생성자가 이미 거부했다.
            state = new ConnectionStateFeature { States = _initialStates };
            features.Set(state);
        }

        // 기본 거부 — 규칙에 없거나(TryGetValue 실패 → allowed=0) 교집합이 비면 끝.
        _rules.TryGetValue(context.Envelope.MessageId.Value, out uint allowed);
        if ((allowed & state.States) == 0)
        {
            return ValueTask.FromResult(DispatchStatus.RejectedByState);
        }

        return next(context);
    }

    /// <summary><see cref="IConnectionStateFeature"/>의 기본 구현.</summary>
    private sealed class ConnectionStateFeature : IConnectionStateFeature
    {
        public uint States { get; set; }
    }
}
