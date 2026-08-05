using System;
using System.Buffers;

namespace ChServerM.Serialization.FlatBuffers.Tests;

/// <summary>배열을 다중 세그먼트 <see cref="ReadOnlySequence{T}"/> 로 쪼개는 테스트 도우미.</summary>
internal static class SequenceFactory
{
    public static ReadOnlySequence<byte> Split(byte[] data, int segmentSize)
    {
        Segment first = new(data.AsMemory(0, Math.Min(segmentSize, data.Length)), runningIndex: 0);
        Segment last = first;

        for (int offset = segmentSize; offset < data.Length; offset += segmentSize)
        {
            int length = Math.Min(segmentSize, data.Length - offset);
            last = last.Append(data.AsMemory(offset, length));
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            Segment next = new(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
