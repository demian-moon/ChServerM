using System;
using System.Buffers;
using System.Threading;
using ChServerM.Connections;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Time;

namespace ChServerM.Dispatch;

/// <summary>
/// 지금 처리 중인 메시지 하나의 문맥.
/// </summary>
/// <remarks>
/// <para>
/// <b>커넥션당 하나를 만들어 프레임마다 재사용한다.</b> 그래서 메시지당 할당이 0이다.
/// <c>ref struct</c>가 아닌 이유는 핸들러가 <c>async</c>이고 <c>ref struct</c>는
/// <c>await</c>를 넘지 못하기 때문이다.
/// </para>
/// <para>
/// <b>가장 중요한 계약 — <see cref="Payload"/>의 수명.</b>
/// 핸들러가 반환하는 순간 무효가 된다. 디스패처는 핸들러가 끝나기 전에
/// <c>AdvanceTo</c>를 부르지 않고, 끝난 직후 <see cref="EndFrame"/>으로 참조를 지운다.
/// 페이로드를 <c>await</c> 너머로 들고 가야 한다면 <b>반드시 복사</b>한다.
/// </para>
/// <para>
/// 레거시는 이 계약을 주석으로만 적어두고 <c>ToArray()</c>로 위반했다.
/// 여기서는 <see cref="EndFrame"/>이 참조를 실제로 끊어 <b>사용 후 해제를 관측 가능하게</b> 만든다.
/// </para>
/// <para>
/// <b>재사용되는 객체다.</b> 인스턴스를 저장하면 다음 프레임의 데이터를 보게 된다.
/// </para>
/// </remarks>
public sealed class MessageContext
{
    private readonly FeatureCollection _features = new();

    /// <summary>커넥션에 묶인 문맥을 만든다.</summary>
    /// <param name="connection">이 문맥이 속한 커넥션.</param>
    public MessageContext(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Connection = connection;
    }

    /// <summary>이 메시지가 도착한 커넥션.</summary>
    public IConnection Connection { get; }

    /// <summary>현재 프레임의 헤더.</summary>
    public FrameHeader Header { get; private set; }

    /// <summary>현재 프레임의 페이로드.</summary>
    /// <remarks><b>핸들러가 반환할 때까지만 유효하다.</b></remarks>
    public ReadOnlySequence<byte> Payload { get; private set; }

    /// <summary>프레임을 읽어낸 시각.</summary>
    /// <remarks>
    /// 단조 시각이다. 처리 지연 계산에 쓰며, 벽시계 역행에 흔들리지 않는다.
    /// </remarks>
    public MonotonicTimestamp ReceivedAt { get; private set; }

    /// <summary>이 메시지에만 붙는 확장 데이터.</summary>
    /// <remarks>
    /// 미들웨어가 뒤 단계로 정보를 넘기는 통로다(인증 주체, 추적 span 등).
    /// <b>프레임마다 비워진다</b> — 커넥션 단위로 유지할 것은
    /// <see cref="IConnection.Features"/>에 둔다.
    /// </remarks>
    public IFeatureCollection Features => _features;

    /// <summary>이 메시지 처리의 취소 토큰.</summary>
    public CancellationToken CancellationToken { get; private set; }

    /// <summary>새 프레임을 처리하도록 문맥을 준비한다.</summary>
    /// <param name="header">프레임 헤더.</param>
    /// <param name="payload">프레임 페이로드.</param>
    /// <param name="receivedAt">읽어낸 시각.</param>
    /// <param name="cancellationToken">이 메시지 처리의 취소 토큰.</param>
    /// <remarks>
    /// <b>프레임워크 내부용이다.</b> 애플리케이션 코드가 부르면 처리 중인 프레임이 뒤바뀐다.
    /// </remarks>
    public void BeginFrame(
        in FrameHeader header,
        in ReadOnlySequence<byte> payload,
        MonotonicTimestamp receivedAt,
        CancellationToken cancellationToken)
    {
        Header = header;
        Payload = payload;
        ReceivedAt = receivedAt;
        CancellationToken = cancellationToken;
    }

    /// <summary>프레임 처리를 끝내고 참조를 지운다.</summary>
    /// <remarks>
    /// <para>
    /// <b>반드시 <c>finally</c>에서 부른다.</b> 핸들러가 예외를 던져도 페이로드 참조가
    /// 남으면 안 된다 — 남은 참조는 이미 반납된 버퍼를 가리킨다.
    /// </para>
    /// <para><b>프레임워크 내부용이다.</b></para>
    /// </remarks>
    public void EndFrame()
    {
        Header = default;
        Payload = default;
        ReceivedAt = MonotonicTimestamp.None;
        CancellationToken = default;
        _features.Reset();
    }
}
