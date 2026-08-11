using System;
using System.Buffers;
using System.Threading;

namespace ChServerM.RealTime.Rooms;

/// <summary>
/// 참조 계수 브로드캐스트 프레임 — 헤더+페이로드를 한 번 조립하고 N 개 싱크가 공유한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — "직렬화 1회"의 실체.</b> 같은 페이로드를 N 명에게 보낼 때 멤버마다
/// 직렬화·헤더 인코딩을 반복하면 브로드캐스트 비용이 N 배가 된다. 이 타입은 완성된 프레임
/// 바이트를 한 벌만 들고, 각 싱크는 자기 파이프에 <b>바이트 복사만</b> 한다.
/// </para>
/// <para>
/// <b>수명 규약.</b> 버퍼는 <see cref="ArrayPool{T}"/> 대여물이고, 마지막 참조를 놓는
/// <see cref="Release"/>가 반납한다. 참조 규칙은 <see cref="IRoomMemberSink.TryDeliver"/>
/// 문서가 정본이다. 큐잉된 프레임의 최악 미처리 대여량은
/// <b>송신 큐 깊이 × 멤버 수</b>에 비례한다 — 풀 크기를 정할 때 이 곱을 계산한다(ADR-0051
/// 이 실측으로 고정한 규약과 같은 계산).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 조립(<see cref="System.Buffers.IBufferWriter{T}"/> 경로)은
/// 브로드캐스터 단일 스레드에서 끝나고, 그 뒤로는 읽기 전용이다.
/// <see cref="AddReference"/>/<see cref="Release"/>는 아무 스레드에서나 안전하다.
/// </para>
/// </remarks>
public sealed class BroadcastFrame : IBufferWriter<byte>
{
    private readonly ArrayPool<byte> _pool;
    private byte[] _buffer;
    private int _written;
    private int _referenceCount;

    internal BroadcastFrame(ArrayPool<byte> pool, int initialCapacity)
    {
        _pool = pool;
        _buffer = pool.Rent(initialCapacity);
    }

    /// <summary>다음 재사용을 위한 풀 링크. <see cref="RoomBroadcaster"/>의 프레임 풀 전용.</summary>
    internal BroadcastFrame? PoolNext;

    /// <summary>조립이 끝난 프레임 바이트.</summary>
    public ReadOnlyMemory<byte> Written => _buffer.AsMemory(0, _written);

    /// <summary>참조를 하나 더 얻는다. 브로드캐스터가 싱크에 넘기기 전에 부른다.</summary>
    internal void AddReference() => Interlocked.Increment(ref _referenceCount);

    /// <summary>초기 참조(조립자 몫)를 설정한다. 재사용 시 상태도 초기화한다.</summary>
    internal void Reset()
    {
        _written = 0;
        Volatile.Write(ref _referenceCount, 1);
    }

    /// <summary>참조를 하나 놓는다. 마지막 참조가 버퍼를 풀로 되돌린다.</summary>
    /// <remarks>정확히 한 번 규약은 <see cref="IRoomMemberSink.TryDeliver"/> 문서가 정본이다.</remarks>
    public void Release()
    {
        int remaining = Interlocked.Decrement(ref _referenceCount);
        if (remaining == 0)
        {
            _written = 0;
            _owner?.ReturnFrame(this);
        }
        else if (remaining < 0)
        {
            // 이중 해제는 곧 풀 이중 반납이다 — 조용히 넘어가면 다른 프레임의 데이터가 오염된다.
            throw new InvalidOperationException("BroadcastFrame 이 참조 수보다 많이 해제됐다.");
        }
    }

    private RoomBroadcaster? _owner;

    /// <summary>프레임을 소유 브로드캐스터에 연결한다. 해제 시 반환처가 된다.</summary>
    internal void Attach(RoomBroadcaster owner) => _owner = owner;

    /// <summary>내부 버퍼를 풀에 반납한다. 브로드캐스터 폐기 경로 전용.</summary>
    internal void ReturnBuffer()
    {
        byte[] buffer = _buffer;
        _buffer = [];
        if (buffer.Length > 0)
        {
            _pool.Return(buffer);
        }
    }

    /// <inheritdoc />
    public void Advance(int count) => _written += count;

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        int required = _written + Math.Max(sizeHint, 1);
        if (required <= _buffer.Length)
        {
            return;
        }

        // 2배 대여-복사-반납 (ADR-0016 의 초과 크기 규약과 동일).
        byte[] grown = _pool.Rent(Math.Max(required, _buffer.Length * 2));
        _buffer.AsSpan(0, _written).CopyTo(grown);
        _pool.Return(_buffer);
        _buffer = grown;
    }
}
