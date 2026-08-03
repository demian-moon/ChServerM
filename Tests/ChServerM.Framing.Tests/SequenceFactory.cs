using System;
using System.Buffers;
using System.Collections.Generic;

namespace ChServerM.Framing.Tests;

/// <summary>
/// 임의의 세그먼트 경계를 갖는 <see cref="ReadOnlySequence{T}"/>를 만든다.
/// </summary>
/// <remarks>
/// <para>
/// <b>이 헬퍼가 없으면 프레이밍 테스트는 의미가 없다.</b> 배열 하나로 만든
/// <see cref="ReadOnlySequence{T}"/>는 항상 단일 세그먼트라 <c>FirstSpan</c> 빠른 경로만
/// 타고, <b>실전에서 반드시 발생하는</b> 경계 넘김 경로가 한 번도 실행되지 않는다.
/// </para>
/// <para>
/// TCP 세그먼트 경계는 프레임 경계를 존중하지 않는다. 16바이트 헤더가 두 세그먼트에
/// 걸치는 일은 흔하고, 여기서 무너지는 구현이 대부분이다.
/// </para>
/// </remarks>
internal static class SequenceFactory
{
    /// <summary>일정한 크기로 잘린 세그먼트 시퀀스를 만든다.</summary>
    /// <param name="data">원본 바이트.</param>
    /// <param name="segmentSize">각 세그먼트의 크기. 1이면 바이트마다 세그먼트가 된다.</param>
    public static ReadOnlySequence<byte> Segmented(ReadOnlySpan<byte> data, int segmentSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentSize);

        List<int> sizes = [];
        for (int offset = 0; offset < data.Length; offset += segmentSize)
        {
            sizes.Add(Math.Min(segmentSize, data.Length - offset));
        }

        return Segmented(data, sizes);
    }

    /// <summary>지정한 크기 목록대로 잘린 세그먼트 시퀀스를 만든다.</summary>
    /// <param name="data">원본 바이트.</param>
    /// <param name="segmentSizes">각 세그먼트의 크기. 합이 <paramref name="data"/> 길이와 같아야 한다.</param>
    public static ReadOnlySequence<byte> Segmented(ReadOnlySpan<byte> data, IReadOnlyList<int> segmentSizes)
    {
        ArgumentNullException.ThrowIfNull(segmentSizes);

        if (data.Length == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        MemorySegment? first = null;
        MemorySegment? current = null;
        int offset = 0;

        foreach (int size in segmentSizes)
        {
            byte[] chunk = data.Slice(offset, size).ToArray();
            offset += size;

            if (first is null)
            {
                first = new MemorySegment(chunk);
                current = first;
            }
            else
            {
                current = current!.Append(chunk);
            }
        }

        if (first is null || current is null || offset != data.Length)
        {
            throw new ArgumentException("세그먼트 크기의 합이 원본 길이와 다르다.", nameof(segmentSizes));
        }

        return new ReadOnlySequence<byte>(first, 0, current, current.Memory.Length);
    }

    private sealed class MemorySegment : ReadOnlySequenceSegment<byte>
    {
        public MemorySegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public MemorySegment Append(ReadOnlyMemory<byte> memory)
        {
            MemorySegment next = new(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
