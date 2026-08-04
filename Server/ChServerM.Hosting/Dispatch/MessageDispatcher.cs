using System;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;

namespace ChServerM.Hosting.Dispatch;

/// <summary>
/// 조립 시점에 확정된 라우팅 테이블로 메시지를 핸들러에 보낸다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 읽기 루프가 애플리케이션을 알지 못하게 하는 경계다. 전송 코드는
/// 프레임을 꺼내 이것 하나만 부르고, 역직렬화·미들웨어·핸들러 호출은 전부 이 뒤에 있다.
/// </para>
/// <para>
/// <b>조회는 배열 인덱싱이다.</b> 메시지 식별자가 <see cref="ushort"/>이므로 등록된
/// 최대 ID 크기의 배열 하나면 해시도 비교도 없이 O(1)이다.
/// 레거시는 프레임마다 선형 탐색 + 가상 호출 n번을 했다.
/// </para>
/// <para>
/// <b>미들웨어는 라우팅보다 앞에 있다.</b> 그래서 등록되지 않은 메시지에도 인증과
/// 속도 제한이 적용된다. 라우팅을 먼저 하면 모르는 ID 를 보내는 것만으로
/// 미들웨어를 우회할 수 있다.
/// </para>
/// <para>
/// <b>예외를 밖으로 흘리지 않는다.</b> 핸들러 하나의 예외로 읽기 루프가 죽으면
/// 멀쩡한 후속 메시지까지 잃는다. 결과를 값으로 돌려 호출자가 "닫을지 계속할지"를 정한다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 불변이므로 스레드 안전하다. 모든 커넥션이 하나를 공유한다.
/// </para>
/// <para>
/// <b>할당.</b> 동기적으로 끝나는 핸들러에 대해 메시지당 힙 할당 0.
/// </para>
/// </remarks>
public sealed class MessageDispatcher : IMessageDispatcher
{
    private static readonly EventId HandlerFaultedEvent = new(4002, "HandlerFaulted");

    private readonly MessageDelegate _pipeline;
    private readonly IServerLogger _logger;

    /// <summary>파이프라인을 받아 디스패처를 만든다.</summary>
    /// <param name="pipeline">미들웨어와 라우팅이 이미 엮인 델리게이트.</param>
    /// <param name="logger">진단 로거.</param>
    /// <exception cref="ArgumentNullException">인자가 <see langword="null"/>일 때.</exception>
    /// <remarks>
    /// 직접 만들지 않고 <see cref="MessageDispatcherBuilder"/>를 쓴다.
    /// 이 생성자는 커스텀 파이프라인을 꽂기 위한 확장점이다.
    /// </remarks>
    public MessageDispatcher(MessageDelegate pipeline, IServerLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        _pipeline = pipeline;
        _logger = logger ?? NullServerLogger.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<DispatchStatus> DispatchAsync(MessageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            return await _pipeline(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 커넥션 종료·서버 종료로 인한 취소는 오류가 아니다. 별도 상태로 구분한다.
            return DispatchStatus.Canceled;
        }
#pragma warning disable CA1031 // 의도적으로 모든 예외를 잡는다 — 아래 주석 참조.
        catch (Exception exception)
        {
            // 핸들러는 애플리케이션 코드다. 무엇을 던질지 알 수 없고, 알 필요도 없다.
            // 여기서 걸러내지 않으면 예외 하나가 읽기 루프를 죽여 그 커넥션의
            // 정상 메시지까지 전부 잃는다. 잡되 반드시 기록한다 — 조용히 삼키면
            // 레거시와 같은 실패(원인 불명의 처리 누락)가 된다.
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.Log(
                    LogLevel.Error,
                    HandlerFaultedEvent,
                    context.Envelope.MessageId.Value,
                    exception,
                    static (messageId, ex) => $"메시지 {messageId} 핸들러가 예외를 던졌다: {ex?.Message}");
            }

            return DispatchStatus.Faulted;
        }
#pragma warning restore CA1031
    }
}
