using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ChServerM.Transport.InMemory;

/// <summary>
/// 이름으로 서버와 클라이언트를 이어주는 프로세스 내 레지스트리.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 실제 전송에서 OS 가 하는 일 — "이 주소를 듣고 있는 게 누구인가" —
/// 를 대신한다.
/// </para>
/// <para>
/// <b>정적(static)이 아니다.</b> 인스턴스마다 독립된 이름 공간을 갖는다. 정적이었다면
/// 병렬로 도는 테스트들이 같은 이름을 두고 충돌하고, 한 테스트가 남긴 리스너가
/// 다음 테스트에 보인다. xUnit 은 클래스 단위로 병렬 실행하므로 이것은 이론이 아니라
/// 실제로 터질 문제다.
/// </para>
/// <para><b>스레드 규약.</b> 스레드 안전하다.</para>
/// </remarks>
public sealed class InMemoryTransportHub
{
    private readonly ConcurrentDictionary<string, InMemoryServerTransport> _listeners =
        new(StringComparer.Ordinal);

    /// <summary>현재 수용 중인 종단 수.</summary>
    public int ListenerCount => _listeners.Count;

    /// <summary>이 이름을 듣고 있는 리스너가 있는지 확인한다.</summary>
    /// <param name="name">종단 이름.</param>
    /// <returns>있으면 <see langword="true"/>.</returns>
    public bool IsListening(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _listeners.ContainsKey(name);
    }

    /// <summary>리스너를 등록한다.</summary>
    /// <returns>같은 이름이 이미 있으면 <see langword="false"/>.</returns>
    internal bool TryRegister(string name, InMemoryServerTransport transport) =>
        _listeners.TryAdd(name, transport);

    /// <summary>리스너를 해제한다.</summary>
    /// <remarks>
    /// <b>자기 자신일 때만</b> 지운다. 같은 이름으로 다른 전송이 이미 다시 바인드했다면
    /// 그것을 지워버리는 것은 명백한 사고다.
    /// </remarks>
    internal void Unregister(string name, InMemoryServerTransport transport) =>
        _listeners.TryRemove(new KeyValuePair<string, InMemoryServerTransport>(name, transport));

    /// <summary>이름에 해당하는 리스너를 찾는다.</summary>
    internal bool TryGetListener(string name, out InMemoryServerTransport transport) =>
        _listeners.TryGetValue(name, out transport!);
}
