using System;

namespace ChServerM.RealTime;

/// <summary>
/// 마이크로초 단조 시계. 생성 시각을 0으로 하는 µs 경과값을 낸다 — 시간 동기화의 전송 단위다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 주파수 환산의 소멸.</b> 레거시는 서버가 로그인 응답에
/// <c>Stopwatch.Frequency</c>(<c>FbsLoginOk.serverFrequency</c>)를 실어 보내고 클라이언트가
/// 환산 계수(<c>gClientTickWeight</c>)를 곱했다. 문제 인식(머신마다 주파수가 다르다)은
/// 정확했지만, 환산 자체가 0 나누기·주파수 미수신 상태라는 결함 표면이었다.
/// <b>양쪽이 처음부터 µs 고정 단위로 말하면 환산이 존재하지 않는다</b>(ADR-0063) —
/// 이 타입이 그 단위의 원천이다.
/// </para>
/// <para>
/// <b>수명 규약.</b> 기준점은 인스턴스 생성 시각이다. 서버는 프로세스 수명 동안 인스턴스
/// 하나를 공유한다 — 재시작하면 기준점이 바뀌므로 클라이언트는 재접속마다 재동기화한다.
/// 이 값은 <b>영속화·머신 간 절대 비교 금지</b>다(<see cref="Time.MonotonicTimestamp"/>와
/// 같은 규약). 프로토콜에 실어 보내는 것은 허용이다 — 받는 쪽이 "그 서버의 시계"로만 쓴다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 불변 상태 + 순수 계산 — 스레드 안전.
/// </para>
/// <para>
/// <b>정밀도 근거.</b> <c>double</c> 경유(레거시 <c>GTickMs</c>, 1년 가동 시 밀리초 뭉개짐)
/// 대신 몫·나머지 정수 분해(<see cref="MicrosecondArithmetic"/>)라 수십 년 가동에도 1µs
/// 미만 오차다.
/// </para>
/// </remarks>
public sealed class MicrosecondClock
{
    private readonly TimeProvider _timeProvider;
    private readonly long _originRaw;
    private readonly long _frequency;

    /// <summary>시계를 만든다. 이 순간이 0µs 다.</summary>
    /// <param name="timeProvider">시간 원본. 테스트에서 대체할 수 있다.</param>
    public MicrosecondClock(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _frequency = timeProvider.TimestampFrequency;
        MicrosecondArithmetic.ValidateFrequency(_frequency);
        _originRaw = timeProvider.GetTimestamp();
    }

    /// <summary>생성 시각부터 지금까지의 경과 마이크로초.</summary>
    public long CurrentMicros() =>
        MicrosecondArithmetic.ToMicros(_timeProvider.GetTimestamp() - _originRaw, _frequency);
}
