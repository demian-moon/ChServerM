using System;
using System.Buffers;
using ChServerM.Diagnostics;
using ChServerM.Framing;
using ChServerM.Identity;
using Xunit;

namespace ChServerM.Core.Tests.Framing;

/// <summary>
/// "데이터가 더 필요하다"와 "잘못됐다"의 구분이 이 타입의 존재 이유다.
/// 레거시는 이 둘을 섞어 손상된 프레임 하나가 커넥션 전체를 오염시켰다.
/// </summary>
public sealed class FrameDecodeResultTests
{
    private static ReadOnlySequence<byte> Buffer(int length) => new(new byte[length]);

    [Fact]
    public void Decoded_IsNotFatal_AndCarriesHeaderAndPayload()
    {
        ReadOnlySequence<byte> buffer = Buffer(64);
        FrameHeader header = new(new MessageId(7), 16);
        ReadOnlySequence<byte> payload = buffer.Slice(FrameHeader.Size, 16);

        FrameDecodeResult result = FrameDecodeResult.Decoded(header, payload, buffer.GetPosition(32));

        Assert.Equal(FrameDecodeStatus.Decoded, result.Status);
        Assert.True(result.IsDecoded);
        Assert.False(result.IsFatal);
        Assert.Equal(header, result.Header);
        Assert.Equal(16, result.Payload.Length);
        Assert.Equal(ErrorCode.None, result.ToErrorCode());
    }

    [Fact]
    public void Decoded_ConsumedEqualsExamined()
    {
        // 프레임을 온전히 읽었으면 검사할 것이 남아 있지 않다.
        ReadOnlySequence<byte> buffer = Buffer(64);
        SequencePosition end = buffer.GetPosition(32);

        FrameDecodeResult result = FrameDecodeResult.Decoded(default, default, end);

        Assert.Equal(result.Consumed, result.Examined);
    }

    [Fact]
    public void NeedMoreData_IsNeitherDecodedNorFatal()
    {
        ReadOnlySequence<byte> buffer = Buffer(8);

        FrameDecodeResult result = FrameDecodeResult.NeedMoreData(buffer.Start, buffer.End);

        Assert.Equal(FrameDecodeStatus.NeedMoreData, result.Status);
        Assert.False(result.IsDecoded);
        Assert.False(result.IsFatal);
        Assert.Equal(ErrorCode.None, result.ToErrorCode());
    }

    [Fact]
    public void NeedMoreData_KeepsConsumedAndExaminedSeparate()
    {
        // examined 가 버퍼 끝이어야 파이프가 더 읽는다. 여기를 틀리면 교착이다.
        ReadOnlySequence<byte> buffer = Buffer(8);

        FrameDecodeResult result = FrameDecodeResult.NeedMoreData(buffer.Start, buffer.End);

        Assert.Equal(8, buffer.Slice(result.Consumed).Length);   // 아무것도 소비하지 않았다
        Assert.Equal(0, buffer.Slice(result.Examined).Length);   // 버퍼 끝까지 봤다
    }

    [Theory]
    [InlineData(FrameDecodeStatus.Malformed, ErrorCode.MalformedFrame)]
    [InlineData(FrameDecodeStatus.TooLarge, ErrorCode.FrameTooLarge)]
    [InlineData(FrameDecodeStatus.VersionMismatch, ErrorCode.ProtocolVersionMismatch)]
    public void Failed_IsFatal_AndMapsToErrorCode(FrameDecodeStatus status, ErrorCode expected)
    {
        ReadOnlySequence<byte> buffer = Buffer(8);

        FrameDecodeResult result = FrameDecodeResult.Failed(status, buffer.Start);

        Assert.True(result.IsFatal);
        Assert.False(result.IsDecoded);
        Assert.Equal(expected, result.ToErrorCode());
    }

    [Theory]
    [InlineData(FrameDecodeStatus.Decoded)]
    [InlineData(FrameDecodeStatus.NeedMoreData)]
    public void Failed_WithNonFailureStatus_Throws(FrameDecodeStatus status)
    {
        // 성공을 실패로 포장하는 실수를 컴파일 대신 런타임에서라도 잡는다.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FrameDecodeResult.Failed(status, Buffer(8).Start));
    }

    [Fact]
    public void EveryFatalStatus_HasAnErrorCode()
    {
        // 새 실패 상태를 추가하고 매핑을 잊으면 원인이 None 으로 기록된다.
        foreach (FrameDecodeStatus status in Enum.GetValues<FrameDecodeStatus>())
        {
            if (status is FrameDecodeStatus.Decoded or FrameDecodeStatus.NeedMoreData)
            {
                continue;
            }

            Assert.NotEqual(ErrorCode.None, FrameDecodeResult.Failed(status, Buffer(1).Start).ToErrorCode());
        }
    }
}
