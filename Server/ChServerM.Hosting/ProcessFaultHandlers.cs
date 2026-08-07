using System;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Diagnostics;

namespace ChServerM.Hosting;

/// <summary>
/// 프로세스 전역 실패 신호(미처리 예외·관측되지 않은 태스크 예외)를 로그로 연결하는
/// <b>선택적</b> 헬퍼 (Phase 10 크래시 처리, ADR-0028).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 프레임워크는 자기 경계 안의 예외를 전부 격리하지만(핸들러는 커넥션 단위,
/// 파티션 작업은 항목 단위, 수락 루프는 <c>_acceptFault</c>), <b>그 밖에서 죽는 경우</b>는
/// 여전히 있다 — 애플리케이션 코드가 만든 스레드, 잊힌 fire-and-forget 태스크, 프레임워크가
/// 만들지 않은 타이머. 그 마지막 순간을 기록하지 못하면 <b>프로세스가 왜 죽었는지 아무도
/// 모른다</b>. 이 헬퍼가 그 기록을 붙인다.
/// </para>
/// <para>
/// <b>⚠ 기본으로 설치하지 않는다 — 프로세스는 호스트의 것이다.</b> <see cref="AppDomain"/>·
/// <see cref="TaskScheduler"/> 이벤트는 <b>프로세스 전역</b>이라, 라이브러리가 조립 시점에
/// 몰래 걸면 (a) 한 프로세스에 서버가 둘 이상일 때 중복 기록되고 (b) 호스트가 이미 건 정책과
/// 충돌한다. 그래서 <b>호스트가 명시적으로 부를 때만</b> 연결하고, <see cref="IDisposable"/> 로
/// 되돌릴 수 있게 한다(테스트·다중 호스팅).
/// </para>
/// <para>
/// <b>동작을 바꾸지 않는다 — 기록만 한다.</b> 미처리 예외에서 프로세스를 살리려 하지 않고
/// (<c>UnhandledException</c> 은 이미 되돌릴 수 없는 지점이다),
/// <c>UnobservedTaskException</c> 에 <c>SetObserved()</c> 를 부르지 않는다 — 그것을 부르면
/// "삼켜도 되는 예외"로 만들어 버려 진짜 버그를 감춘다. .NET Core 의 기본 동작(프로세스를
/// 죽이지 않음)을 그대로 두고 <b>보이게만</b> 한다.
/// </para>
/// <para>
/// <b>덤프 수집과 재시작은 코드가 아니라 운영 설정이다(ADR-0028).</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>덤프</b> — .NET 런타임이 환경변수로 지원한다:
///     <c>DOTNET_DbgEnableMiniDump=1</c>, <c>DOTNET_DbgMiniDumpType=4</c>(전체),
///     <c>DOTNET_DbgMiniDumpName=/dumps/chserverm.%p.dmp</c>. 관리 코드로 덤프를 뜨려면
///     플랫폼별 P/Invoke 가 필요하고 Native AOT 와 충돌하므로, <b>런타임 기능을 쓰는 쪽이
///     맞다</b> — 컨테이너라면 볼륨을 붙이고 이 변수를 건다.
///   </description></item>
///   <item><description>
///     <b>재시작</b> — 오케스트레이터(Kubernetes·systemd)의 몫이다. 프레임워크의 기여는
///     <b>헬스를 정직하게 보고하고</b>(<c>/healthz</c>·<c>/readyz</c>) 죽을 때 로그를 남기는
///     것까지다. 프로세스가 스스로를 재시작하면 오케스트레이터의 백오프·이벤트 기록을 우회해
///     장애가 보이지 않게 된다.
///   </description></item>
/// </list>
/// <para><b>스레드 규약.</b> <see cref="Install"/> 은 조립 스레드에서 한 번 부른다. 콜백은 런타임이 임의 스레드에서 호출한다.</para>
/// </remarks>
public static class ProcessFaultHandlers
{
    private static readonly EventId UnhandledEvent = new(9000, "UnhandledException");
    private static readonly EventId UnobservedEvent = new(9001, "UnobservedTaskException");

    /// <summary>프로세스 전역 실패 신호를 로거에 연결한다.</summary>
    /// <param name="logger">기록할 로거.</param>
    /// <returns>연결을 해제하는 핸들. 프로세스 수명 동안 유지하려면 버리지 말고 보관한다.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/>가 <see langword="null"/>일 때.</exception>
    /// <remarks>
    /// <b>여러 번 부르면 여러 번 기록된다</b> — 이 헬퍼는 중복 설치를 막지 않는다. 프로세스당
    /// 한 번 부르는 것이 전제이며, 되돌리려면 반환된 핸들을 <c>Dispose</c> 한다.
    /// </remarks>
    public static IDisposable Install(IServerLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return new Subscription(logger);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly IServerLogger _logger;
        private readonly UnhandledExceptionEventHandler _onUnhandled;
        private readonly EventHandler<UnobservedTaskExceptionEventArgs> _onUnobserved;
        private int _disposed;

        public Subscription(IServerLogger logger)
        {
            _logger = logger;

            // 델리게이트를 필드에 보관한다 — 해제하려면 구독할 때와 같은 인스턴스가 필요하다.
            _onUnhandled = OnUnhandled;
            _onUnobserved = OnUnobserved;

            AppDomain.CurrentDomain.UnhandledException += _onUnhandled;
            TaskScheduler.UnobservedTaskException += _onUnobserved;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            AppDomain.CurrentDomain.UnhandledException -= _onUnhandled;
            TaskScheduler.UnobservedTaskException -= _onUnobserved;
        }

        private void OnUnhandled(object? sender, UnhandledExceptionEventArgs e)
        {
            // 프로세스가 곧 죽는다. 이 로그가 원인에 대한 마지막 기록이므로 Critical 이다.
            // 로거가 비동기 버퍼링을 한다면 여기서 유실될 수 있다 — 그래서 덤프(런타임 환경변수)가
            // 별도로 필요하다(타입 문서).
            if (!_logger.IsEnabled(LogLevel.Critical))
            {
                return;
            }

            _logger.Log(
                LogLevel.Critical,
                UnhandledEvent,
                e.IsTerminating,
                e.ExceptionObject as Exception,
                static (terminating, ex) =>
                    $"미처리 예외로 프로세스가 {(terminating ? "종료된다" : "계속된다")}: {ex?.Message}");
        }

        private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            // SetObserved() 를 부르지 않는다 — 부르면 "삼켜도 되는 예외" 로 만들어 진짜 버그를
            // 감춘다. 기본 동작을 그대로 두고 보이게만 한다(타입 문서).
            if (!_logger.IsEnabled(LogLevel.Error))
            {
                return;
            }

            _logger.Log(
                LogLevel.Error,
                UnobservedEvent,
                0,
                e.Exception,
                static (_, ex) =>
                    $"관측되지 않은 태스크 예외 — 어딘가에서 await 를 빠뜨렸다는 신호다: {ex?.Message}");
        }
    }
}
