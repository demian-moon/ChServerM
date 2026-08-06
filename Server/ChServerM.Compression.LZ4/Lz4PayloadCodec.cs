using System;
using System.Buffers;
using System.Buffers.Binary;
using K4os.Compression.LZ4;

namespace ChServerM.Compression.LZ4;

/// <summary>
/// <see cref="IPayloadCodec"/>의 LZ4 블록 어댑터 (ADR-0019).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 실시간 메시지 압축의 정석 — 압축률보다 속도(GB/s 급)를 택하는
/// 알고리즘이다. 레거시도 LZ4 를 의도했으나 <c>maxLength &gt;= originDataLen</c> 이
/// 항상 참이라 <b>한 번도 실행되지 않았다</b>(legacy/07-security 결함 #1) —
/// "압축이 실제로 실행됨"이 이 어댑터 테스트의 고정 대상이다.
/// </para>
/// <para>
/// <b>블롭 형식(이 코덱의 와이어 계약):</b>
/// <c>[원본 길이 u32 LE, 4바이트][LZ4 블록]</c>.
/// 원본 길이가 앞에 있어야 <see cref="TryDecode"/> 가 버퍼를 잡기 전에 상한을 검사한다
/// (T-18 — 레거시 T-12 의 역). varint 가 아니라 고정 4바이트인 이유: 이 접두는
/// 코덱 내부 형식이라 프레이밍 축(varint 코덱)을 참조할 수 없고, 4바이트 절약보다
/// 형식 단순성이 낫다.
/// </para>
/// <para>
/// <b>압축 레벨은 FAST 고정이다.</b> HC 레벨은 압축률 이득 대비 수십 배 느리다 —
/// 실시간 경로의 기본값이 될 수 없다. 필요해지면 레벨 인자는 벤치마크 수치와 함께
/// 추가한다(측정 없는 최적화 금지의 역방향 — 측정 없는 옵션 표면도 만들지 않는다).
/// </para>
/// <para><b>스레드 규약.</b> 무상태다. 모든 커넥션이 공유해도 안전하다.</para>
/// <para><b>할당.</b> 힙 할당 0 — 다중 세그먼트 해제 입력만 풀 대여 1회(finally 반납).</para>
/// </remarks>
public sealed class Lz4PayloadCodec : IPayloadCodec
{
    /// <summary>블롭 접두(원본 길이 u32 LE) 크기.</summary>
    public const int HeaderSize = 4;

    /// <inheritdoc />
    public int MaxEncodedLength(int sourceLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        return sourceLength == 0 ? HeaderSize : HeaderSize + LZ4Codec.MaximumOutputSize(sourceLength);
    }

    /// <inheritdoc />
    public int Encode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < MaxEncodedLength(source.Length))
        {
            throw new ArgumentException(
                $"압축 출력 버퍼가 짧다. 필요: {MaxEncodedLength(source.Length)}, 받은 크기: {destination.Length}. " +
                $"{nameof(MaxEncodedLength)} 로 산정한 버퍼를 넘긴다.",
                nameof(destination));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)source.Length);

        if (source.IsEmpty)
        {
            return HeaderSize;
        }

        int encoded = LZ4Codec.Encode(source, destination[HeaderSize..]);
        if (encoded < 0)
        {
            // MaximumOutputSize 버퍼에서는 도달 불가 — 도달했다면 벤더 계약이 깨진 것이다.
            throw new InvalidOperationException("LZ4 인코딩이 산정된 최대 크기 버퍼에서 실패했다.");
        }

        return HeaderSize + encoded;
    }

    /// <inheritdoc />
    public bool TryDecode(
        in ReadOnlySequence<byte> source,
        IBufferWriter<byte> destination,
        int maxDecodedLength,
        out int decodedLength)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDecodedLength);

        decodedLength = 0;

        if (source.Length < HeaderSize)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        source.Slice(0, HeaderSize).CopyTo(header);
        uint claimed = BinaryPrimitives.ReadUInt32LittleEndian(header);

        // 선언 길이는 버퍼를 잡기 전에 검증한다 — 이 순서가 T-18/T-12 완화의 실체다.
        if (claimed > (uint)maxDecodedLength)
        {
            return false;
        }

        long compressedLength = source.Length - HeaderSize;

        if (claimed == 0)
        {
            // 빈 페이로드 블롭 — 블록이 붙어 있으면 형식 위반이다.
            return compressedLength == 0;
        }

        if (compressedLength <= 0 || compressedLength > int.MaxValue)
        {
            return false;
        }

        ReadOnlySequence<byte> compressed = source.Slice(HeaderSize);
        Span<byte> target = destination.GetSpan((int)claimed)[..(int)claimed];

        int decoded;
        if (compressed.IsSingleSegment)
        {
            decoded = DecodeBlock(compressed.FirstSpan, target);
        }
        else
        {
            // LZ4 블록 디코더는 연속 입력이 필요하다. 압축 입력은 프레이밍 상한 이하이므로
            // 풀 대여 복사가 안전하다 — 반납은 finally(수명 규약).
            byte[] rented = ArrayPool<byte>.Shared.Rent((int)compressedLength);
            try
            {
                compressed.CopyTo(rented);
                decoded = DecodeBlock(rented.AsSpan(0, (int)compressedLength), target);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        // 선언과 실제가 다르면(손상·알고리즘 불일치·조작) 커밋하지 않는다 —
        // Advance 전이므로 destination 에는 아무것도 반영되지 않는다.
        if (decoded != (int)claimed)
        {
            return false;
        }

        destination.Advance(decoded);
        decodedLength = decoded;
        return true;
    }

    /// <summary>벤더 디코더를 값 계약으로 감싼다 — 실패는 음수.</summary>
    /// <remarks>
    /// K4os 는 형식이 깨진 입력에 음수를 돌려주지만, 악의적 입력 전체에 대해 "던지지
    /// 않는다"가 벤더의 문서화된 계약은 아니다. 원격 입력의 실패를 값으로 바꾸는 것은
    /// 어댑터의 몫이다(<c>AspNetIdentityPasswordHasher</c> 의 <c>FormatException</c> 변환과
    /// 같은 원칙, T-16).
    /// </remarks>
    private static int DecodeBlock(ReadOnlySpan<byte> compressed, Span<byte> target)
    {
        try
        {
            return LZ4Codec.Decode(compressed, target);
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
        catch (ArgumentException)
        {
            return -1;
        }
        catch (IndexOutOfRangeException)
        {
            return -1;
        }
    }
}
