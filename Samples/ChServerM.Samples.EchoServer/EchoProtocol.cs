using System;
using System.Threading.Tasks;
using ChServerM.Dispatch;
using ChServerM.Framing;
using ChServerM.Hosting;
using ChServerM.Identity;

namespace ChServerM.Samples.EchoServer;

/// <summary>
/// 이 샘플이 쓰는 메시지 식별자.
/// </summary>
/// <remarks>
/// 앱 대역(1~40000)을 쓴다. 프레임워크 대역(40001~)을 침범하면 하트비트와 충돌한다.
/// </remarks>
internal static class EchoProtocol
{
    /// <summary>받은 페이로드를 그대로 돌려달라는 요청.</summary>
    public static MessageId Echo => new(1);

    /// <summary>서버가 지금까지 처리한 프레임 수를 달라는 요청.</summary>
    public static MessageId Stats => new(2);
}

/// <summary>
/// 에코 핸들러.
/// </summary>
/// <remarks>
/// <para>
/// <b>이 클래스가 이 샘플의 요점이다.</b> 여기 있는 코드는 다음을 하나도 알지 못한다.
/// </para>
/// <list type="bullet">
///   <item><description>전송이 TCP 인지 인메모리인지</description></item>
///   <item><description>프레임 헤더가 몇 바이트인지</description></item>
///   <item><description>어느 스레드에서 도는지 (파티션 실행 모델이 알아서 고정한다)</description></item>
/// </list>
/// <para>
/// 그런데도 <see cref="Program"/> 은 이 <b>같은 인스턴스</b>를 두 전송에 꽂아 돌린다.
/// 그것이 ADR-0004 가 요구하는 조립 가능성의 합격 기준이다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 파티션 실행 모델을 쓰면 같은 커넥션의 메시지는 같은 스레드에서
/// 순차 실행된다. 하지만 <b>다른 커넥션은 다른 스레드에서 동시에</b> 이 인스턴스를
/// 호출한다 — 그래서 카운터를 <see cref="System.Threading.Interlocked"/> 로 갱신한다.
/// </para>
/// </remarks>
internal sealed class EchoHandler(IFrameEncoder encoder)
{
    private long _framesHandled;

    /// <summary>지금까지 처리한 프레임 수.</summary>
    public long FramesHandled => System.Threading.Interlocked.Read(ref _framesHandled);

    /// <summary>받은 페이로드를 그대로 돌려보낸다.</summary>
    /// <remarks>
    /// 페이로드를 <c>ToArray()</c> 로 평탄화하지 않고 시퀀스 그대로 흘려보낸다.
    /// 이것이 제로 카피 경로다 — 레거시는 여기서 패킷당 5~8회 할당을 했다.
    /// </remarks>
    public async ValueTask<DispatchStatus> HandleEchoAsync(MessageContext context)
    {
        System.Threading.Interlocked.Increment(ref _framesHandled);

        await FrameWriter.WriteFrameAsync(
            context.Connection.Output,
            encoder,
            context.Header.MessageId,
            context.Payload,
            FrameFlags.None,
            context.Header.Sequence,
            context.CancellationToken).ConfigureAwait(false);

        return DispatchStatus.Handled;
    }

    /// <summary>처리한 프레임 수를 8바이트 리틀 엔디안으로 돌려보낸다.</summary>
    public async ValueTask<DispatchStatus> HandleStatsAsync(MessageContext context)
    {
        System.Threading.Interlocked.Increment(ref _framesHandled);

        byte[] payload = new byte[sizeof(long)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(payload, FramesHandled);

        await FrameWriter.WriteFrameAsync(
            context.Connection.Output,
            encoder,
            context.Header.MessageId,
            payload,
            FrameFlags.None,
            context.Header.Sequence,
            context.CancellationToken).ConfigureAwait(false);

        return DispatchStatus.Handled;
    }
}
