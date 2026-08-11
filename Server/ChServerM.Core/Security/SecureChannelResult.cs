using System;
using System.Diagnostics;
using ChServerM.Diagnostics;

namespace ChServerM.Security;

/// <summary>
/// <see cref="ITransportSecurity"/> 핸드셰이크 한 번의 결과.
/// </summary>
/// <remarks>
/// <para>
/// 실패를 예외가 아니라 값으로 나른다 — 잘못된 핸드셰이크의 폭주가 공격
/// 시나리오(THREAT-MODEL T-16)라, 커넥션당 예외는 공격 비용을 서버가 증폭하는
/// 구조가 된다. <c>FrameDecodeResult</c>와 같은 목적 전용 결과 구조체 규약이다.
/// </para>
/// <para>
/// <see cref="Channel"/>은 <see cref="SecureChannelStatus.Established"/>일 때만
/// 유효하다. 그 순간부터 채널 정리 책임은 호출자(호스팅)에게 넘어간다 —
/// 반납 책임 단일 원칙(ADR-0016 과 같은 발상).
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct SecureChannelResult : IEquatable<SecureChannelResult>
{
    private SecureChannelResult(SecureChannelStatus status, ISecureChannel? channel)
    {
        Status = status;
        Channel = channel;
    }

    /// <summary>핸드셰이크 결과 상태.</summary>
    public SecureChannelStatus Status { get; }

    /// <summary>확립된 보안 채널. <see cref="Status"/>가 <see cref="SecureChannelStatus.Established"/>일 때만 유효하다.</summary>
    public ISecureChannel? Channel { get; }

    /// <summary>보안 채널이 확립됐는지 여부.</summary>
    public bool IsEstablished => Status == SecureChannelStatus.Established;

    /// <summary>확립 결과를 만든다.</summary>
    /// <param name="channel">확립된 보안 채널.</param>
    /// <exception cref="ArgumentNullException"><paramref name="channel"/>이 null 일 때 — 채널 없는 "확립됨"은 표현할 수 없어야 한다.</exception>
    public static SecureChannelResult Established(ISecureChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new SecureChannelResult(SecureChannelStatus.Established, channel);
    }

    /// <summary>실패 결과를 만든다.</summary>
    /// <param name="status">실패 종류.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/>가 실패를 나타내지 않을 때.</exception>
    public static SecureChannelResult Failed(SecureChannelStatus status)
    {
        if (status is SecureChannelStatus.Established or SecureChannelStatus.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status), status,
                "실패 상태가 아니다. 확립된 채널은 Established(...) 팩토리로 만들고, None 은 '시도 없음' 센티넬이라 결과가 될 수 없다.");
        }

        return new SecureChannelResult(status, channel: null);
    }

    /// <summary>이 결과에 대응하는 오류 코드를 구한다.</summary>
    /// <returns>확립이면 <see cref="ErrorCode.None"/>. 센티넬(<see cref="SecureChannelStatus.None"/>)은 조립 버그이므로 <see cref="ErrorCode.Internal"/>로 드러낸다 — 조용히 지나가게 두지 않는다.</returns>
    public ErrorCode ToErrorCode() => Status switch
    {
        SecureChannelStatus.Established => ErrorCode.None,
        SecureChannelStatus.HandshakeFailed => ErrorCode.SecureChannelFailed,
        SecureChannelStatus.Canceled => ErrorCode.OperationCanceled,
        _ => ErrorCode.Internal,
    };

    /// <inheritdoc />
    public bool Equals(SecureChannelResult other) =>
        Status == other.Status && ReferenceEquals(Channel, other.Channel);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SecureChannelResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Status, Channel);

    /// <summary>두 결과가 같은지 비교한다.</summary>
    public static bool operator ==(SecureChannelResult left, SecureChannelResult right) => left.Equals(right);

    /// <summary>두 결과가 다른지 비교한다.</summary>
    public static bool operator !=(SecureChannelResult left, SecureChannelResult right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => Status.ToString();
}
