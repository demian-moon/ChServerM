using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Identity;
using ChServerM.Resilience;
using ChServerM.Sessions;

namespace ChServerM.Hosting;

/// <summary>
/// 세션 저장소를 서킷 브레이커로 감싸 <b>죽은 저장소로 가는 호출을 끊는다</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 외부 세션 저장소(Redis 등)가 죽으면 모든 요청이 타임아웃까지 기다리며
/// 스레드·커넥션을 붙잡는다. <b>저장소 하나의 장애가 서버 전체를 멈추는 경로</b>가 그것이고,
/// 이 데코레이터가 그것을 끊는다. Phase 13 의 "외부 저장소 장애 시 동작" 이며 ADR-0027 의
/// 보류를 푸는 첫 실물이다.
/// </para>
///
/// <para>
/// <b>데코레이터인 이유.</b> 횡단 관심사는 코어 로직을 오염시키지 않는다(CLAUDE.md 4절).
/// 어댑터마다 브레이커를 심으면 어댑터 수만큼 같은 코드가 생기고, 그 중 하나가 규율을
/// 어겨도 아무도 모른다. <b>인메모리 저장소에도 그대로 씌울 수 있다</b>(의미는 없지만
/// 테스트에는 유용하다) — 그것이 축이 제대로 잘렸다는 증거다.
/// </para>
///
/// <para>
/// <b>⚠⚠ 무엇을 실패로 세는가 — 이 타입의 가장 중요한 결정.</b>
/// </para>
/// <list type="bullet">
///   <item><b>실패가 아닌 것</b>: <c>SessionWriteResult.Conflict</c>(CAS 충돌),
///   <c>SessionReadResult.NotFound</c>. 이것들은 <b>저장소가 정상적으로 '아니오' 라고 답한
///   것</b>이다. 실패로 세면 <b>경합이 심할 때 멀쩡한 저장소를 차단</b>하게 되고, 그것은
///   부하를 견디라고 만든 장치가 부하 때문에 서비스를 끊는 정반대 결과다</item>
///   <item><b>실패가 아닌 것 2</b>: <see cref="ArgumentException"/> 계열,
///   <see cref="ObjectDisposedException"/>, <see cref="OperationCanceledException"/>.
///   <b>호출자 버그와 취소는 대상의 건강과 무관</b>하다. 이것을 세면 잘못된 코드 한 줄이
///   저장소를 차단시킨다</item>
///   <item><b>실패인 것</b>: 그 밖의 모든 예외 — 연결 끊김, 타임아웃, 프로토콜 오류.
///   즉 <b>"저장소가 대답하지 않았다"</b></item>
/// </list>
/// <para>
/// 분류는 <see cref="CircuitBreakingSessionStore(ISessionStore, ICircuitBreaker, Func{Exception, bool}?)"/>
/// 의 술어로 교체할 수 있다 — 어댑터가 자기 벤더 예외를 더 잘 알기 때문이다.
/// </para>
///
/// <para>
/// <b>⚠ 열려 있을 때는 예외를 던진다.</b> 세션 계약의 반환 타입에는 "저장소가 대답하지
/// 않았다" 를 표현할 자리가 없다. <c>NotFound</c> 로 접으면 호출자가 "세션이 없다" 로 읽어
/// <b>새 세션을 만들고, 그것이 사용자 상태 유실</b>이다 —
/// <see cref="CircuitOpenException"/> 문서 참조.
/// </para>
///
/// <para>
/// <b>스레드 규약.</b> 내부 저장소와 브레이커가 스레드 안전한 만큼 안전하다.
/// 이 타입 자체는 상태를 갖지 않는다.
/// </para>
///
/// <para>
/// <b>수명·소유권 규약.</b> 감싼 저장소의 소유권은 <b>호출자에게 있다</b> — 이 데코레이터는
/// 그것을 닫지 않는다.
/// </para>
/// </remarks>
public sealed class CircuitBreakingSessionStore : ISessionStore
{
    private readonly ISessionStore _inner;
    private readonly ICircuitBreaker _breaker;
    private readonly Func<Exception, bool> _shouldCountAsFailure;

    /// <summary>세션 저장소를 서킷 브레이커로 감싼다.</summary>
    /// <param name="inner">감쌀 저장소. <b>소유권은 호출자에게 있다.</b></param>
    /// <param name="breaker">서킷 브레이커.</param>
    /// <param name="shouldCountAsFailure">
    /// 예외를 대상 장애로 셀지 판정한다. <see langword="null"/> 이면
    /// <see cref="IsInfrastructureFailure"/> 를 쓴다.
    /// </param>
    /// <exception cref="ArgumentNullException">필수 인자가 <see langword="null"/> 이다.</exception>
    public CircuitBreakingSessionStore(
        ISessionStore inner,
        ICircuitBreaker breaker,
        Func<Exception, bool>? shouldCountAsFailure = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(breaker);

        _inner = inner;
        _breaker = breaker;
        _shouldCountAsFailure = shouldCountAsFailure ?? IsInfrastructureFailure;
    }

    /// <summary>기본 분류 — 대상의 건강과 무관한 예외를 걸러낸다.</summary>
    /// <param name="exception">판정할 예외.</param>
    /// <returns>대상 장애로 셀 것이면 <see langword="true"/>.</returns>
    /// <remarks>
    /// 호출자 버그(<see cref="ArgumentException"/> 계열)와 취소는 저장소가 아픈 것이 아니다.
    /// 이것을 세면 <b>잘못된 코드 한 줄이 저장소를 차단시킨다</b>.
    /// </remarks>
    public static bool IsInfrastructureFailure(Exception exception) =>
        exception is not (ArgumentException or ObjectDisposedException or OperationCanceledException);

    /// <inheritdoc/>
    public async ValueTask<SessionReadResult> TryReadAsync(
        SessionId id,
        IBufferWriter<byte> destination,
        CancellationToken cancellationToken = default)
    {
        Enter();
        bool succeeded = false;
        try
        {
            SessionReadResult result = await _inner.TryReadAsync(id, destination, cancellationToken)
                .ConfigureAwait(false);

            // ⚠ NotFound 는 실패가 아니다 — 저장소가 정상적으로 답했다.
            succeeded = true;
            return result;
        }
        catch (Exception ex) when (Report(ex))
        {
            throw; // Report 는 항상 false 를 반환한다 — 필터를 부작용 지점으로만 쓴다.
        }
        finally
        {
            // 성공 보고를 finally 에 두어 어떤 경로로 빠져나가도 시험 자리가 반납되게 한다(9.2).
            if (succeeded)
            {
                _breaker.RecordSuccess();
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask<SessionWriteResult> TryWriteAsync(
        SessionId id,
        ReadOnlyMemory<byte> state,
        SessionVersion expectedVersion,
        TimeSpan? timeToLive = null,
        CancellationToken cancellationToken = default)
    {
        Enter();
        bool succeeded = false;
        try
        {
            SessionWriteResult result = await _inner
                .TryWriteAsync(id, state, expectedVersion, timeToLive, cancellationToken)
                .ConfigureAwait(false);

            // ⚠⚠ Conflict 도 실패가 아니다. 이것을 실패로 세면 경합이 심할 때 멀쩡한 저장소를
            // 차단하게 된다 — 부하를 견디라고 만든 장치가 부하 때문에 서비스를 끊는다.
            succeeded = true;
            return result;
        }
        catch (Exception ex) when (Report(ex))
        {
            throw;
        }
        finally
        {
            if (succeeded)
            {
                _breaker.RecordSuccess();
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask<bool> TryRemoveAsync(
        SessionId id,
        SessionVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        Enter();
        bool succeeded = false;
        try
        {
            bool result = await _inner.TryRemoveAsync(id, expectedVersion, cancellationToken)
                .ConfigureAwait(false);

            succeeded = true;
            return result;
        }
        catch (Exception ex) when (Report(ex))
        {
            throw;
        }
        finally
        {
            if (succeeded)
            {
                _breaker.RecordSuccess();
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask<bool> TryRenewAsync(
        SessionId id,
        SessionVersion expectedVersion,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        Enter();
        bool succeeded = false;
        try
        {
            bool result = await _inner.TryRenewAsync(id, expectedVersion, timeToLive, cancellationToken)
                .ConfigureAwait(false);

            succeeded = true;
            return result;
        }
        catch (Exception ex) when (Report(ex))
        {
            throw;
        }
        finally
        {
            if (succeeded)
            {
                _breaker.RecordSuccess();
            }
        }
    }

    /// <summary>서킷을 지나간다. 열려 있으면 던진다.</summary>
    private void Enter()
    {
        if (!_breaker.TryEnter())
        {
            throw CircuitOpenException.ForCircuit(_breaker.Name);
        }
    }

    /// <summary>
    /// 예외를 분류해 필요하면 실패로 보고한다. <b>항상 <see langword="false"/> 를 반환한다.</b>
    /// </summary>
    /// <remarks>
    /// 예외 필터를 부작용 지점으로 쓴다 — <c>catch</c> 블록에서 보고하면 스택을 되감은 뒤가
    /// 되지만, 필터는 <b>되감기 전에</b> 실행되어 진단 시 원래 스택이 보존된다. 항상
    /// <see langword="false"/> 이므로 이 필터가 예외를 삼키지 않는다.
    /// </remarks>
    private bool Report(Exception exception)
    {
        if (_shouldCountAsFailure(exception))
        {
            _breaker.RecordFailure(exception);
        }
        else
        {
            // 대상의 건강과 무관한 예외 — 성공도 실패도 아니지만 시험 자리는 반납해야 한다.
            _breaker.RecordSuccess();
        }

        return false;
    }
}
