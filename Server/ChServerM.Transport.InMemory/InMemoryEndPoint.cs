using System;
using System.Net;

namespace ChServerM.Transport.InMemory;

/// <summary>
/// 이름으로 식별되는 프로세스 내 종단.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <see cref="ChServerM.Transports.IServerTransport"/>·
/// <see cref="ChServerM.Transports.IClientTransport"/>의 계약이
/// <see cref="EndPoint"/>를 쓰므로, 인메모리 전송도 같은 타입으로 자기 주소를 표현해야 한다.
/// 여기서 <c>string</c> 을 쓰거나 인터페이스에 오버로드를 추가했다면
/// <b>전송 교체 가능성이 그 자리에서 깨진다.</b>
/// </para>
/// <para>
/// 이름은 대소문자를 구분한다. 포트 번호처럼 임의의 식별자일 뿐이고,
/// 대소문자 무시 규칙을 도입하면 문화권 의존 비교가 끼어든다.
/// </para>
/// <para><b>스레드 규약.</b> 불변이므로 스레드 안전하다.</para>
/// </remarks>
public sealed class InMemoryEndPoint : EndPoint, IEquatable<InMemoryEndPoint>
{
    /// <summary>이름으로 종단을 만든다.</summary>
    /// <param name="name">이 종단의 식별자. 비어 있을 수 없다.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/>이 <see langword="null"/>이거나 공백일 때.</exception>
    public InMemoryEndPoint(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>이 종단의 식별자.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public bool Equals(InMemoryEndPoint? other) =>
        other is not null && string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as InMemoryEndPoint);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <inheritdoc />
    public override string ToString() => $"inmem://{Name}";
}
