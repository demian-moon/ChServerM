using System;
using System.Buffers;

namespace ChServerM.Framing;

/// <summary>
/// LEB128 가변 길이 정수(varint)의 읽기·쓰기.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> varint 프레이밍의 와이어 표현을 아는 코드가 이 한 곳뿐이어야 한다
/// (<see cref="FrameHeaderCodec"/>과 같은 원리 — 읽기와 쓰기가 어긋나면 프레임 경계가
/// 통째로 밀린다).
/// </para>
/// <para>
/// <b>정규형(canonical)만 받는다.</b> 같은 값의 varint 표현이 여러 개면
/// (예: <c>0x00</c> 과 <c>0x80 0x00</c> 모두 0) 리플레이 방지·AEAD 태그 같은
/// 바이트 단위 검증이 전부 흔들린다. 최소 길이 표현이 아니면
/// <see cref="Status.Malformed"/>다.
/// </para>
/// <para><b>스레드 규약.</b> 상태 없는 정적 클래스다. 어디서 불러도 안전하다.</para>
/// <para><b>할당.</b> 힙 할당이 없다. <see cref="SequenceReader{T}"/>는 ref struct 다.</para>
/// </remarks>
internal static class VarintCodec
{
    /// <summary>u32 varint 의 최대 길이(바이트). 32비트는 7비트 그룹 5개면 끝난다.</summary>
    internal const int MaxUInt32Bytes = 5;

    /// <summary>varint 읽기 한 번의 결과.</summary>
    internal enum Status : byte
    {
        /// <summary>값을 온전히 읽었다.</summary>
        Decoded = 0,

        /// <summary>버퍼가 varint 중간에서 끝났다. 더 읽고 다시 시도한다.</summary>
        NeedMoreData = 1,

        /// <summary>비정규 표현이거나 u32 범위를 넘는다. 커넥션을 닫아야 하는 실패다.</summary>
        Malformed = 2,
    }

    /// <summary>u32 varint 하나를 읽는다.</summary>
    /// <param name="reader">읽기 위치. 성공·실패와 무관하게 소비한 만큼 전진한다.</param>
    /// <param name="value">성공하면 읽어낸 값.</param>
    /// <returns>읽기 결과.</returns>
    /// <remarks>
    /// 실패 시 <paramref name="reader"/> 위치를 되돌리지 않는다 — varint 프레이밍의
    /// 실패는 전부 커넥션 종료라 되돌릴 이유가 없고, 호출자(디코더)는 실패 시
    /// 버퍼 시작 위치를 그대로 보고한다.
    /// </remarks>
    internal static Status TryReadUInt32(ref SequenceReader<byte> reader, out uint value)
    {
        value = 0;
        int shift = 0;

        while (true)
        {
            if (!reader.TryRead(out byte current))
            {
                value = 0;
                return Status.NeedMoreData;
            }

            // 정규형 강제 — 이어지는 바이트가 0이면(예: 0x80 0x00) 같은 값의 더 짧은
            // 표현이 존재한다.
            if (current == 0 && shift != 0)
            {
                value = 0;
                return Status.Malformed;
            }

            if (shift == 28 && (current & 0xF0) != 0)
            {
                // 다섯째 바이트는 하위 4비트만 유효하다. 그 위 비트(연장 비트 포함)가
                // 켜져 있으면 u32 를 넘거나 5바이트를 넘는 varint 다.
                value = 0;
                return Status.Malformed;
            }

            value |= (uint)(current & 0x7F) << shift;

            if ((current & 0x80) == 0)
            {
                return Status.Decoded;
            }

            shift += 7;
        }
    }

    /// <summary>u32 varint 하나를 쓴다.</summary>
    /// <param name="destination">쓸 대상. <see cref="MaxUInt32Bytes"/> 이상이어야 한다.</param>
    /// <param name="value">쓸 값.</param>
    /// <returns>쓴 바이트 수(1~5). 항상 최소 길이(정규형)다.</returns>
    internal static int Write(Span<byte> destination, uint value)
    {
        int written = 0;

        while (value >= 0x80)
        {
            destination[written++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[written++] = (byte)value;
        return written;
    }

    /// <summary>값을 varint 로 쓰면 몇 바이트인지 구한다.</summary>
    /// <param name="value">잴 값.</param>
    /// <returns>1~5.</returns>
    internal static int Measure(uint value)
    {
        int bytes = 1;

        while (value >= 0x80)
        {
            value >>= 7;
            bytes++;
        }

        return bytes;
    }
}
