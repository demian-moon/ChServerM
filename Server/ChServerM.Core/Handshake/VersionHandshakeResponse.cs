using System;

namespace ChServerM.Handshake;

/// <summary>
/// 서버가 <c>ClientHello</c> 에 보낸 응답 — 확정(<c>ServerHello</c>) 또는 거부(<c>ConnectionRejected</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 클라이언트 입장에서 협상 응답은 두 갈래(확정/거부)인데, 두 갈래를
/// 별도 파싱 API 로 나누면 호출자가 "어느 것을 먼저 시도하는가"를 틀릴 수 있다.
/// 한 번의 파싱이 두 갈래를 모두 판별해 이 값으로 돌려준다.
/// </para>
/// <para>
/// 실패를 예외가 아니라 값으로 나르는 이유는 <c>SecureChannelResult</c> 와 같다 —
/// 원격 입력이 만드는 실패 경로에 예외를 쓰면 악의적 입력이 비용을 증폭시킨다(T-16).
/// </para>
/// </remarks>
public readonly struct VersionHandshakeResponse : IEquatable<VersionHandshakeResponse>
{
    internal VersionHandshakeResponse(
        bool isAccepted,
        ushort selectedVersion,
        ushort rejectReason,
        ProtocolVersionRange serverSupported,
        int frameSize)
    {
        IsAccepted = isAccepted;
        SelectedVersion = selectedVersion;
        RejectReason = rejectReason;
        ServerSupported = serverSupported;
        FrameSize = frameSize;
    }

    /// <summary>서버가 버전을 확정했으면 <see langword="true"/>, 거부했으면 <see langword="false"/>.</summary>
    public bool IsAccepted { get; }

    /// <summary>확정된 버전. <see cref="IsAccepted"/>가 아니면 0(센티넬).</summary>
    public ushort SelectedVersion { get; }

    /// <summary>
    /// 거부 사유의 동결 수치. 확정이면 0.
    /// </summary>
    /// <remarks>
    /// <see cref="VersionHandshakeCodec.RejectReasonVersionMismatch"/> 가 현재 유일하게
    /// 정의된 값이다. 모르는 값이 와도 파싱은 성공한다 — 사유 코드는 진단용이지
    /// 분기 근거가 아니다(거부는 거부다).
    /// </remarks>
    public ushort RejectReason { get; }

    /// <summary>거부 시 서버가 알려온 지원 구간. 확정이면 <see langword="default"/>(센티넬).</summary>
    /// <remarks>클라이언트가 "업데이트가 필요하다"를 사용자에게 알릴 근거다(R-3).</remarks>
    public ProtocolVersionRange ServerSupported { get; }

    /// <summary>이 응답이 차지한 와이어 바이트 수. 파이프에서 소비할 길이다.</summary>
    public int FrameSize { get; }

    /// <inheritdoc />
    public bool Equals(VersionHandshakeResponse other) =>
        IsAccepted == other.IsAccepted
        && SelectedVersion == other.SelectedVersion
        && RejectReason == other.RejectReason
        && ServerSupported == other.ServerSupported
        && FrameSize == other.FrameSize;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is VersionHandshakeResponse other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(IsAccepted, SelectedVersion, RejectReason, ServerSupported, FrameSize);

    /// <summary>두 응답이 같은지 비교한다.</summary>
    public static bool operator ==(VersionHandshakeResponse left, VersionHandshakeResponse right) => left.Equals(right);

    /// <summary>두 응답이 다른지 비교한다.</summary>
    public static bool operator !=(VersionHandshakeResponse left, VersionHandshakeResponse right) => !left.Equals(right);
}
