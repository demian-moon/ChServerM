using System;
using System.Buffers;
using ChServerM.Handshake;
using Xunit;

namespace ChServerM.Core.Tests.Handshake;

/// <summary>
/// 동결 핸드셰이크 코덱의 퍼징 불변식 — 협상 이전의 코덱은 순수 원격 입력을 파싱하므로
/// <b>어떤 바이트에도 던지지 않아야 한다</b>(T-16: 예외는 비용 증폭 경로다).
/// </summary>
/// <remarks>
/// 시드 고정 무작위 — 실패가 나면 같은 시드로 재현된다(프레이밍 퍼징과 같은 방식).
/// </remarks>
public sealed class VersionHandshakeCodecFuzzTests
{
    private const int Iterations = 5_000;
    private const int Seed = 20260806;

    [Fact]
    public void Random_bytes_never_throw_and_yield_defined_statuses()
    {
        Random random = new(Seed);
        byte[] buffer = new byte[64];

        for (int i = 0; i < Iterations; i++)
        {
            int length = random.Next(0, buffer.Length + 1);
            random.NextBytes(buffer.AsSpan(0, length));
            ReadOnlySequence<byte> sequence = new(buffer.AsMemory(0, length));

            VersionHandshakeStatus clientHello =
                VersionHandshakeCodec.TryReadClientHello(sequence, out _);
            VersionHandshakeStatus serverResponse =
                VersionHandshakeCodec.TryReadServerResponse(sequence, out _);

            // None(센티넬)이 관측되면 초기화 누락 버그다.
            Assert.NotEqual(VersionHandshakeStatus.None, clientHello);
            Assert.NotEqual(VersionHandshakeStatus.None, serverResponse);
        }
    }

    [Fact]
    public void Single_byte_mutations_of_valid_frames_never_throw()
    {
        byte[] hello = new byte[VersionHandshakeCodec.ClientHelloFrameSize];
        VersionHandshakeCodec.WriteClientHello(hello, new ProtocolVersionRange(1, 3));

        byte[] serverHello = new byte[VersionHandshakeCodec.ServerHelloFrameSize];
        VersionHandshakeCodec.WriteServerHello(serverHello, 2);

        byte[] rejection = new byte[VersionHandshakeCodec.RejectionFrameSize];
        VersionHandshakeCodec.WriteRejection(rejection, new ProtocolVersionRange(1, 1));

        // 유효 프레임의 전 오프셋 × 전 비트 뒤집기 — 경계 필드 하나가 깨졌을 때의 전수 검사.
        MutateAll(hello, static mutated =>
            VersionHandshakeCodec.TryReadClientHello(new ReadOnlySequence<byte>(mutated), out _));
        MutateAll(serverHello, static mutated =>
            VersionHandshakeCodec.TryReadServerResponse(new ReadOnlySequence<byte>(mutated), out _));
        MutateAll(rejection, static mutated =>
            VersionHandshakeCodec.TryReadServerResponse(new ReadOnlySequence<byte>(mutated), out _));
    }

    [Fact]
    public void Parsing_is_deterministic()
    {
        Random random = new(Seed);
        byte[] buffer = new byte[VersionHandshakeCodec.RejectionFrameSize];

        for (int i = 0; i < Iterations; i++)
        {
            random.NextBytes(buffer);
            ReadOnlySequence<byte> sequence = new(buffer);

            VersionHandshakeStatus first =
                VersionHandshakeCodec.TryReadServerResponse(sequence, out VersionHandshakeResponse firstResponse);
            VersionHandshakeStatus second =
                VersionHandshakeCodec.TryReadServerResponse(sequence, out VersionHandshakeResponse secondResponse);

            Assert.Equal(first, second);
            Assert.Equal(firstResponse, secondResponse);
        }
    }

    private static void MutateAll(byte[] valid, Action<byte[]> parse)
    {
        byte[] mutated = new byte[valid.Length];

        for (int offset = 0; offset < valid.Length; offset++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                valid.CopyTo(mutated, 0);
                mutated[offset] ^= (byte)(1 << bit);
                parse(mutated); // 불변식: 던지지 않는다. 상태 값 자체는 변조 위치에 따라 다르다.
            }
        }
    }
}
