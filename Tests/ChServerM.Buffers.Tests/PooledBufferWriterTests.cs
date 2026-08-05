using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace ChServerM.Buffers.Tests;

/// <summary>
/// <see cref="PooledBufferWriter"/> 의 계약과 Phase 3 게이트 조건 검증.
/// </summary>
/// <remarks>
/// 게이트 조건 둘이 여기 있다: <see cref="SteadyState_ReuseLoop_AllocatesNothing"/> 이
/// "대여-반납 왕복 힙 할당 0"을, <see cref="IntentionalLeak_IsObservedByFinalizer"/> 가
/// "누수 감지가 의도적 누수를 잡는다"를 고정한다.
/// </remarks>
public sealed class PooledBufferWriterTests
{
    [Fact]
    public void Write_ThenRead_PreservesContent()
    {
        using PooledBufferWriter writer = new();

        for (byte i = 0; i < 100; i++)
        {
            Span<byte> span = writer.GetSpan(3);
            span[0] = i;
            span[1] = (byte)(i + 1);
            span[2] = (byte)(i + 2);
            writer.Advance(3);
        }

        Assert.Equal(300, writer.WrittenCount);
        Assert.Equal(0, writer.WrittenSpan[0]);
        Assert.Equal(99, writer.WrittenSpan[297]);
        Assert.Equal(101, writer.WrittenSpan[299]);
    }

    [Fact]
    public void Growth_PreservesContent()
    {
        // 초기 용량을 최소로 잡아 성장 경로(대여-복사-반납)를 반드시 밟게 한다.
        using PooledBufferWriter writer = new(initialCapacity: 1);

        byte[] expected = new byte[100_000];
        for (int i = 0; i < expected.Length; i++)
        {
            expected[i] = (byte)(i % 251);
        }

        int offset = 0;
        while (offset < expected.Length)
        {
            int chunk = Math.Min(777, expected.Length - offset);
            expected.AsSpan(offset, chunk).CopyTo(writer.GetSpan(chunk));
            writer.Advance(chunk);
            offset += chunk;
        }

        Assert.Equal(expected, writer.WrittenMemory.ToArray());
    }

    [Fact]
    public void Clear_KeepsBuffer_AndResets()
    {
        using PooledBufferWriter writer = new();

        writer.GetSpan(4)[0] = 1;
        writer.Advance(4);
        writer.Clear();

        Assert.Equal(0, writer.WrittenCount);

        writer.GetSpan(1)[0] = 42;
        writer.Advance(1);
        Assert.Equal(42, writer.WrittenSpan[0]);
    }

    [Fact]
    public void SteadyState_ReuseLoop_AllocatesNothing()
    {
        // Phase 3 게이트: 정착 상태의 대여-반납 왕복은 힙 할당 0이어야 한다.
        using PooledBufferWriter writer = new(initialCapacity: 8192);

        // 워밍업 — 성장과 JIT 를 끝낸다.
        WriteFrameLike(writer);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1_000; i++)
        {
            WriteFrameLike(writer);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndBlocksFurtherUse()
    {
        PooledBufferWriter writer = new();
        writer.Dispose();
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.GetSpan(1));
        Assert.Throws<ObjectDisposedException>(writer.Clear);
    }

    [Fact]
    public void Advance_BeyondGrantedSpace_Throws()
    {
        using PooledBufferWriter writer = new();
        _ = writer.GetSpan(1);

        Assert.Throws<InvalidOperationException>(() => writer.Advance(1024 * 1024));
    }

    [Fact]
    public void IntentionalLeak_IsObservedByFinalizer()
    {
        // Phase 3 게이트: 누수 감지가 의도적 누수를 실제로 잡아야 한다.
        long leakedBefore = BufferPoolDiagnostics.LeakedBuffers;

        CreateAndAbandon();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.True(
            BufferPoolDiagnostics.LeakedBuffers > leakedBefore,
            "Dispose 없이 버린 버퍼가 누수로 관측되지 않았다.");
    }

    /// <summary>루트가 남지 않도록 인라이닝을 막고 만든 뒤 버린다.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateAndAbandon()
    {
#pragma warning disable CA2000 // 의도적 누수 — 이 테스트의 목적 그 자체다.
        PooledBufferWriter leaked = new();
#pragma warning restore CA2000
        leaked.GetSpan(16)[0] = 1;
        leaked.Advance(1);
    }

    /// <summary>프레임 하나 분량의 쓰기 패턴 — 헤더 흉내 + 페이로드 청크.</summary>
    private static void WriteFrameLike(PooledBufferWriter writer)
    {
        writer.Clear();

        for (int chunk = 0; chunk < 16; chunk++)
        {
            Span<byte> span = writer.GetSpan(256);
            span[..256].Fill((byte)chunk);
            writer.Advance(256);
        }
    }
}
