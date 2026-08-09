using System;

namespace ChServerM.Sessions;

/// <summary>
/// 세션 읽기의 결과 — 찾았는지, 어떤 버전인지, 몇 바이트를 썼는지.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 세 값을 함께 돌려줘야 한다.</b> "찾았다" 만으로는 이어서 쓸 수 없고
/// (기대 버전이 없으면 CAS 를 못 한다), 버전만으로는 대상 <see cref="System.Buffers.IBufferWriter{T}"/>
/// 에 얼마나 들어갔는지 모른다. 셋을 <see langword="out"/> 매개변수 세 개로 흩으면 호출부가
/// 지저분해지고 하나를 빼먹기 쉽다.
/// </para>
/// <para>
/// <b>구조체인 이유.</b> 세션 조회는 핫패스가 될 수 있다(요청마다 한 번). 결과 하나 때문에
/// 힙 할당이 생기면 안 된다(CLAUDE.md 2절).
/// </para>
/// <para>
/// <b>⚠ <see cref="Length"/> 는 대상에 <i>실제로 쓴</i> 바이트 수다.</b> 찾지 못했으면 0 이고,
/// 그때 저장소는 대상 writer 를 <b>건드리지 않는다</b> — 없는 세션을 읽었다고 해서 대상이
/// 오염되지 않는다는 뜻이다.
/// </para>
/// </remarks>
public readonly struct SessionReadResult : IEquatable<SessionReadResult>
{
    private SessionReadResult(bool found, SessionVersion version, int length)
    {
        Found = found;
        Version = version;
        Length = length;
    }

    /// <summary>세션을 찾았는가. 만료된 항목은 <see langword="false"/> 다.</summary>
    public bool Found { get; }

    /// <summary>
    /// 읽은 시점의 버전. 이 값을 그대로 쓰기의 기대 버전으로 넘겨 CAS 를 완성한다.
    /// 찾지 못했으면 <see cref="SessionVersion.None"/>.
    /// </summary>
    public SessionVersion Version { get; }

    /// <summary>대상 writer 에 실제로 쓴 바이트 수. 찾지 못했으면 0.</summary>
    public int Length { get; }

    /// <summary>세션을 찾지 못했다(또는 만료됐다).</summary>
    public static SessionReadResult NotFound => default;

    /// <summary>세션을 찾았다.</summary>
    /// <param name="version">읽은 시점의 버전.</param>
    /// <param name="length">대상에 쓴 바이트 수.</param>
    /// <returns>찾음 결과.</returns>
    public static SessionReadResult Hit(SessionVersion version, int length) => new(true, version, length);

    /// <inheritdoc/>
    public bool Equals(SessionReadResult other) =>
        Found == other.Found && Version == other.Version && Length == other.Length;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SessionReadResult other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Found, Version, Length);

    /// <summary>두 결과가 같은지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    public static bool operator ==(SessionReadResult left, SessionReadResult right) => left.Equals(right);

    /// <summary>두 결과가 다른지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    public static bool operator !=(SessionReadResult left, SessionReadResult right) => !left.Equals(right);
}
