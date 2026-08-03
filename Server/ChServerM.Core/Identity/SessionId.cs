using System;
using System.Globalization;

namespace ChServerM.Identity;

/// <summary>
/// 세션을 가리키는 강타입 식별자.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConnectionId"/>와 구분한다. 커넥션은 끊겼다 다시 붙을 수 있지만
/// 세션은 재접속을 가로질러 유지될 수 있다. 그래서 세션 식별자는 <b>노드를 넘어 안정</b>해야 하고
/// 영속화·로그에 남을 수 있어야 한다 — <see cref="ObjectId"/>를 기반으로 삼는 이유다.
/// </para>
/// <para>
/// 세션 저장소의 빠른 조회는 별도 슬롯 핸들로 처리한다. 이 타입은 <b>안정 식별자</b> 역할만 한다.
/// </para>
/// </remarks>
public readonly struct SessionId : IEquatable<SessionId>
{
    private readonly ObjectId _value;

    /// <summary><see cref="ObjectId"/>로 세션 식별자를 만든다.</summary>
    public SessionId(ObjectId value) => _value = value;

    /// <summary>설정되지 않은 값.</summary>
    public static SessionId None => default;

    /// <summary>기반 <see cref="ObjectId"/>.</summary>
    public ObjectId Value => _value;

    /// <summary><see cref="None"/>인지 여부.</summary>
    public bool IsNone => _value.IsNone;

    /// <summary>파티션 배정에 쓸 안정 해시 키를 만든다.</summary>
    public PartitionKey ToPartitionKey() => _value.ToPartitionKey();

    /// <inheritdoc />
    public bool Equals(SessionId other) => _value.Equals(other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SessionId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>두 식별자가 같은지 비교한다.</summary>
    public static bool operator ==(SessionId left, SessionId right) => left.Equals(right);

    /// <summary>두 식별자가 다른지 비교한다.</summary>
    public static bool operator !=(SessionId left, SessionId right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"sess:{_value.Value}");
}

/// <summary>
/// 예약된 작업을 가리키는 강타입 식별자.
/// </summary>
/// <remarks>
/// <para>
/// <b>소유자 범위로 한정된다.</b> 같은 키 문자열을 쓰는 서로 다른 오브젝트가 충돌하지 않도록
/// 소유자 식별자를 함께 담는다.
/// </para>
/// <para>
/// 레거시 <c>HashM</c>은 오브젝트 스코프 키(<c>"buff_speed"</c> 등)를 전역 스케줄러의
/// 문자열 작업 ID로 그대로 넘겼다. 두 오브젝트가 같은 키를 쓰면 두 번째부터 등록이 실패해
/// <b>만료가 조용히 동작하지 않았다.</b>
/// </para>
/// </remarks>
public readonly struct JobId : IEquatable<JobId>
{
    private readonly ulong _owner;
    private readonly ulong _local;

    /// <summary>소유자와 지역 번호로 작업 식별자를 만든다.</summary>
    /// <param name="owner">작업을 소유한 주체(세션·오브젝트 등)의 식별자.</param>
    /// <param name="local">소유자 안에서 유일한 번호.</param>
    public JobId(ulong owner, ulong local)
    {
        _owner = owner;
        _local = local;
    }

    /// <summary>설정되지 않은 값.</summary>
    public static JobId None => default;

    /// <summary>작업을 소유한 주체의 식별자.</summary>
    public ulong Owner => _owner;

    /// <summary>소유자 안에서의 지역 번호.</summary>
    public ulong Local => _local;

    /// <summary><see cref="None"/>인지 여부.</summary>
    public bool IsNone => _owner == 0 && _local == 0;

    /// <inheritdoc />
    public bool Equals(JobId other) => _owner == other._owner && _local == other._local;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is JobId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_owner, _local);

    /// <summary>두 식별자가 같은지 비교한다.</summary>
    public static bool operator ==(JobId left, JobId right) => left.Equals(right);

    /// <summary>두 식별자가 다른지 비교한다.</summary>
    public static bool operator !=(JobId left, JobId right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"job:{_owner}/{_local}");
}

/// <summary>클러스터 노드를 가리키는 강타입 식별자.</summary>
/// <remarks><see cref="ObjectId.MaxNodeId"/> 이하여야 <see cref="ObjectId"/>에 담을 수 있다.</remarks>
public readonly struct NodeId : IEquatable<NodeId>
{
    private readonly ushort _value;

    /// <summary>수치로 노드 식별자를 만든다.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/>가 <see cref="ObjectId.MaxNodeId"/>를 넘을 때.
    /// </exception>
    public NodeId(ushort value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, ObjectId.MaxNodeId);
        _value = value;
    }

    /// <summary>설정되지 않은 값.</summary>
    public static NodeId None => default;

    /// <summary>원본 수치.</summary>
    public ushort Value => _value;

    /// <inheritdoc />
    public bool Equals(NodeId other) => _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NodeId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value;

    /// <summary>두 식별자가 같은지 비교한다.</summary>
    public static bool operator ==(NodeId left, NodeId right) => left.Equals(right);

    /// <summary>두 식별자가 다른지 비교한다.</summary>
    public static bool operator !=(NodeId left, NodeId right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"node:{_value}");
}
