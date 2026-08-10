using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace ChServerM.Hosting.Sessions;

/// <summary>
/// 세션 재개 자격 증명 — 재접속할 때 "내가 그 세션의 주인이다" 를 증명하는 32바이트 난수.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — <see cref="ChServerM.Identity.SessionId"/> 를 자격으로 쓸 수 없다.</b>
/// 세션 식별자는 로그·메트릭·진단에 등장하고 추측·열거될 수 있는 값이다. 그것을 재접속
/// 자격으로 쓰면 <b>ID 를 아는 사람이 곧 세션 주인</b>이 되어 세션 탈취가 된다.
/// 자격은 <b>식별자와 분리된 비밀</b>이어야 한다.
/// </para>
///
/// <para>
/// <b>⚠ 저장소에는 원본이 아니라 해시를 둔다.</b> 저장소가 유출돼도 그 값으로 재접속할 수
/// 없어야 한다 — 비밀번호를 평문으로 두지 않는 것과 같은 이유다.
/// 해시는 <see cref="Hash"/> 가 만든다.
/// </para>
///
/// <para>
/// <b>⚠ 사용할 때마다 회전한다.</b> 재개에 성공하면 새 토큰을 발급하고 옛 토큰은 즉시
/// 무효가 된다. 그래서 <b>탈취된 토큰은 1회용</b>이고, 진짜 주인과 탈취자 중 늦게 온 쪽이
/// 실패하므로 <b>탈취 사실이 드러난다</b>. 회전 로직은 호스팅 계층이 담당한다.
/// </para>
///
/// <para>
/// <b>비교는 상수 시간으로 한다.</b> 바이트별 조기 반환 비교는 <b>일치하는 접두사 길이가
/// 응답 시간에 새어</b> 한 바이트씩 맞춰 나가는 공격을 허용한다.
/// <see cref="Equals(SessionResumeToken)"/> 는 <see cref="CryptographicOperations"/> 를 쓴다.
/// </para>
///
/// <para>
/// <b>⚠ 왜 Core 가 아니라 Hosting 에 있는가.</b> 이 타입은 값 타입의 모양을 하고 있지만
/// <b>암호 연산을 수행</b>한다(난수 생성·SHA-256·상수 시간 비교). Core 는 추상화·인터페이스·
/// 값 타입만 담고, 그것을 <c>CoreDependencyTests</c> 가 참조 목록으로 강제한다 —
/// 처음에 Core 에 뒀다가 그 가드에 걸렸고, <b>가드가 옳았다</b>. 그리고 Core 계약 중 재개
/// 토큰을 받는 것이 하나도 없다(재개 메커니즘 전체가 호스팅 계층이다).
/// 클라이언트가 이 타입을 필요로 할 때(Phase 16+)가 Core 로 올릴지 판단할 시점이다 —
/// 두 번째 소비자가 생기기 전의 승격은 이 프로젝트가 경계하는 "가설 추상화" 다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 불변 값 타입이다. 어디서든 안전하게 복사·비교할 수 있다.
/// </para>
/// <para>
/// <b>할당 규약.</b> <see cref="InlineArrayAttribute"/> 로 바이트를 구조체 안에 인라인해
/// <b>토큰 하나당 힙 할당이 0</b>이다(CLAUDE.md 2절).
/// </para>
/// </remarks>
public readonly struct SessionResumeToken : IEquatable<SessionResumeToken>
{
    /// <summary>토큰 길이(바이트). 128비트를 넘는 엔트로피로 추측을 배제한다.</summary>
    public const int Length = 32;

    /// <summary>저장소에 두는 해시의 길이(바이트) — SHA-256.</summary>
    public const int HashLength = 32;

    private readonly TokenBytes _bytes;

    private SessionResumeToken(ReadOnlySpan<byte> value)
    {
        _bytes = default;
        value[..Length].CopyTo(AsWritableSpan(ref _bytes));
    }

    /// <summary>암호학적 난수로 새 토큰을 만든다.</summary>
    /// <returns>새 토큰.</returns>
    public static SessionResumeToken Create()
    {
        Span<byte> buffer = stackalloc byte[Length];
        RandomNumberGenerator.Fill(buffer);
        return new SessionResumeToken(buffer);
    }

    /// <summary>와이어에서 받은 바이트로 토큰을 만든다.</summary>
    /// <param name="value">정확히 <see cref="Length"/> 바이트.</param>
    /// <returns>토큰.</returns>
    /// <exception cref="ArgumentException">길이가 <see cref="Length"/> 가 아니다.</exception>
    public static SessionResumeToken FromBytes(ReadOnlySpan<byte> value)
    {
        if (value.Length != Length)
        {
            throw new ArgumentException($"재개 토큰은 정확히 {Length} 바이트여야 한다. 받은 길이: {value.Length}", nameof(value));
        }

        return new SessionResumeToken(value);
    }

    /// <summary>토큰 바이트를 대상 버퍼에 복사한다. 와이어로 내보낼 때 쓴다.</summary>
    /// <param name="destination">길이 <see cref="Length"/> 이상.</param>
    /// <exception cref="ArgumentException">대상이 짧다.</exception>
    /// <remarks>
    /// <para><b>이 값을 로그에 남기지 않는다.</b> 재접속 자격 그 자체다.</para>
    /// <para>
    /// <b>스팬을 반환하지 않고 복사하는 이유.</b> <c>readonly struct</c> 는 자기 필드에 대한
    /// 스팬을 반환할 수 없다(임시 복사본을 가리킬 수 있어 컴파일러가 막는다). 그리고 비밀
    /// 값에는 <b>호출자가 버퍼를 소유하는 편이 낫다</b> — 다 쓴 뒤 지울 수 있다.
    /// </para>
    /// </remarks>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException($"대상은 {Length} 바이트 이상이어야 한다.", nameof(destination));
        }

        AsReadOnlySpan(in _bytes).CopyTo(destination[..Length]);
    }

    /// <summary>저장소에 둘 해시를 계산한다.</summary>
    /// <param name="destination">길이 <see cref="HashLength"/> 이상.</param>
    /// <exception cref="ArgumentException">대상이 짧다.</exception>
    public void Hash(Span<byte> destination)
    {
        if (destination.Length < HashLength)
        {
            throw new ArgumentException($"해시 대상은 {HashLength} 바이트 이상이어야 한다.", nameof(destination));
        }

        SHA256.HashData(AsReadOnlySpan(in _bytes), destination[..HashLength]);
    }

    /// <summary>제시된 토큰이 저장된 해시와 일치하는지 <b>상수 시간</b>으로 확인한다.</summary>
    /// <param name="storedHash">저장소에 있던 해시(<see cref="HashLength"/> 바이트).</param>
    /// <returns>일치하면 <see langword="true"/>.</returns>
    /// <remarks>
    /// 길이가 다르면 즉시 <see langword="false"/> 다 — 길이는 비밀이 아니다.
    /// </remarks>
    public bool MatchesHash(ReadOnlySpan<byte> storedHash)
    {
        if (storedHash.Length != HashLength)
        {
            return false;
        }

        Span<byte> computed = stackalloc byte[HashLength];
        Hash(computed);
        return CryptographicOperations.FixedTimeEquals(computed, storedHash);
    }

    /// <inheritdoc/>
    /// <remarks>상수 시간 비교다 — 타이밍으로 접두사를 흘리지 않는다.</remarks>
    public bool Equals(SessionResumeToken other) =>
        CryptographicOperations.FixedTimeEquals(AsReadOnlySpan(in _bytes), AsReadOnlySpan(in other._bytes));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SessionResumeToken other && Equals(other);

    /// <inheritdoc/>
    /// <remarks>
    /// 앞 4바이트만 쓴다. 해시 코드는 비밀이 아니어야 할 곳(사전 버킷)에 쓰이므로
    /// 전체를 섞어 넣지 않는다.
    /// </remarks>
    public override int GetHashCode() =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(AsReadOnlySpan(in _bytes));

    /// <summary>진단용 표현. <b>토큰 값을 노출하지 않는다.</b></summary>
    public override string ToString() => "SessionResumeToken(재개 자격 — 값은 표시하지 않는다)";

    /// <summary>두 토큰이 같은지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    public static bool operator ==(SessionResumeToken left, SessionResumeToken right) => left.Equals(right);

    /// <summary>두 토큰이 다른지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    public static bool operator !=(SessionResumeToken left, SessionResumeToken right) => !left.Equals(right);

    private static Span<byte> AsWritableSpan(ref TokenBytes bytes) => bytes;

    private static ReadOnlySpan<byte> AsReadOnlySpan(in TokenBytes bytes) =>
        System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(
            ref System.Runtime.CompilerServices.Unsafe.As<TokenBytes, byte>(
                ref System.Runtime.CompilerServices.Unsafe.AsRef(in bytes)),
            Length);

    /// <summary>토큰 바이트를 구조체 안에 인라인한다 — 힙 할당 0.</summary>
    [InlineArray(Length)]
    private struct TokenBytes
    {
        private byte _element0;
    }
}
