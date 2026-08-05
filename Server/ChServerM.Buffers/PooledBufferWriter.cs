using System;
using System.Buffers;

namespace ChServerM.Buffers;

/// <summary>
/// <see cref="ArrayPool{T}"/> 대여 버퍼 위에서 동작하는 <see cref="IBufferWriter{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 응답 직렬화의 스크래치 버퍼다 — 프레임 헤더에 페이로드 길이가
/// 먼저 필요하므로, 직렬화기는 어딘가에 페이로드를 먼저 써야 한다.
/// <see cref="ArrayBufferWriter{T}"/> 는 그 "어딘가"를 매번 힙에 만든다(요청당 할당).
/// 이 타입은 같은 계약을 풀 대여로 제공해 정상 상태의 할당을 0으로 만든다
/// (수치: BENCHMARKS.md 버퍼 절, ADR-0016).
/// </para>
/// <para>
/// <b>수명·소유권 규약 — 만든 자가 반납한다.</b> 반납은 <see cref="Dispose"/> 하나뿐이다.
/// <c>ref struct</c> 로 스코프를 강제하지 않은 이유는 핸들러가 <c>async</c> 이기 때문이다 —
/// <c>ref struct</c> 는 <c>await</c> 경계를 넘지 못한다(ADR-0016 대안 표).
/// 대신 <b>반납 누락은 조용히 사라지지 않는다</b>: 파이널라이저가 누수를
/// <see cref="BufferPoolDiagnostics.LeakedBuffers"/> 로 관측하고 버퍼를 회수한다.
/// 정상 경로에서는 <see cref="GC.SuppressFinalize"/> 로 파이널라이저 비용이 0이다.
/// 레거시의 <c>ArrayPool</c> 미반납(관측 불가)이 재발할 수 없는 구조다.
/// </para>
/// <para>
/// <b>재사용.</b> <see cref="Clear"/> 는 버퍼를 유지한 채 길이만 되돌린다 —
/// 커넥션당 하나를 만들어 응답마다 재사용하는 것이 의도된 사용법이다.
/// 성장은 2배 대여-복사-반납이라 반복 사용 시 정착 크기에서 더는 자라지 않는다.
/// </para>
/// <para><b>스레드 규약.</b> 단일 소유자 전용이다. 동시 접근은 버퍼를 오염시킨다 —
/// 커넥션 송신 경로가 단일 소유자라는 <c>IConnection</c> 규약과 같은 전제다.</para>
/// </remarks>
public sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[]? _buffer;
    private int _written;

    /// <summary>풀에서 초기 버퍼를 대여해 만든다.</summary>
    /// <param name="initialCapacity">초기 용량 힌트. 풀 특성상 이 이상이 대여될 수 있다.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/>가 0 이하일 때.</exception>
    public PooledBufferWriter(int initialCapacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialCapacity, 0);

        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        BufferPoolDiagnostics.OnRented();
    }

    /// <summary>지금까지 쓴 바이트 수.</summary>
    public int WrittenCount => _written;

    /// <summary>지금까지 쓴 내용. 다음 쓰기·<see cref="Clear"/>·<see cref="Dispose"/> 전까지만 유효하다.</summary>
    public ReadOnlyMemory<byte> WrittenMemory => Written().AsMemory(0, _written);

    /// <summary>지금까지 쓴 내용. 다음 쓰기·<see cref="Clear"/>·<see cref="Dispose"/> 전까지만 유효하다.</summary>
    public ReadOnlySpan<byte> WrittenSpan => Written().AsSpan(0, _written);

    /// <inheritdoc />
    public void Advance(int count)
    {
        byte[] buffer = Written();
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (_written + count > buffer.Length)
        {
            throw new InvalidOperationException(
                $"GetSpan/GetMemory 가 준 것({buffer.Length - _written}B)보다 많이({count}B) 전진했다.");
        }

        _written += count;
    }

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

    /// <summary>버퍼는 유지한 채 쓴 내용을 비운다. 재사용의 핵심 경로다.</summary>
    public void Clear()
    {
        _ = Written();
        _written = 0;
    }

    /// <summary>버퍼를 풀에 반납한다. 여러 번 불러도 안전하다.</summary>
    public void Dispose()
    {
        byte[]? buffer = _buffer;
        if (buffer is null)
        {
            return;
        }

        _buffer = null;
        _written = 0;
        ArrayPool<byte>.Shared.Return(buffer);
        BufferPoolDiagnostics.OnReturned();
        GC.SuppressFinalize(this);
    }

    /// <summary>반납이 누락된 채 수거될 때 — 누수를 관측하고 버퍼를 회수한다.</summary>
    /// <remarks>
    /// 정상 경로(<see cref="Dispose"/>)에서는 실행되지 않는다. 여기 도달했다는 것 자체가
    /// 소유권 규약 위반이며, <see cref="BufferPoolDiagnostics.LeakedBuffers"/> 가 0이 아니면
    /// 어딘가의 반납이 새고 있다는 뜻이다 — 관측되지 않는 유실을 만들지 않는다.
    /// </remarks>
    ~PooledBufferWriter()
    {
        byte[]? buffer = _buffer;
        if (buffer is not null)
        {
            _buffer = null;
            ArrayPool<byte>.Shared.Return(buffer);
            BufferPoolDiagnostics.OnLeaked();
        }
    }

    private byte[] Written()
    {
        byte[]? buffer = _buffer;
        ObjectDisposedException.ThrowIf(buffer is null, this);
        return buffer;
    }

    private void EnsureCapacity(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);

        byte[] buffer = Written();
        int required = _written + Math.Max(sizeHint, 1);

        if (required <= buffer.Length)
        {
            return;
        }

        // 2배 성장 — 반복 성장의 복사 총량이 선형에 머문다.
        byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(required, buffer.Length * 2));
        BufferPoolDiagnostics.OnRented();

        buffer.AsSpan(0, _written).CopyTo(grown);
        _buffer = grown;

        ArrayPool<byte>.Shared.Return(buffer);
        BufferPoolDiagnostics.OnReturned();
    }
}
