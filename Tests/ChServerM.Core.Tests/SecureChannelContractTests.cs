using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Diagnostics;
using ChServerM.Security;
using Xunit;

namespace ChServerM.Core.Tests;

/// <summary>
/// <see cref="ITransportSecurity"/> 계약(ADR-0017)의 사용 규약을 고정하는 테스트.
/// </summary>
/// <remarks>
/// 구현체(TLS)가 아직 없는 단계에서 이 테스트가 고정하는 것은 두 가지다 —
/// (1) 결과 구조체의 제한 규약: 기본값은 "확립됨"이 될 수 없고, 채널 없는 확립·
/// 실패 아닌 실패를 표현할 수 없다(레거시 <c>AllowedPkState</c> 기본 전부 허용 결함의 역).
/// (2) 계약이 벤더 의존 없이 구현 가능하다는 것 — 패스스루 구현이 파이프 왕복을 통과한다.
/// </remarks>
public sealed class SecureChannelContractTests
{
    // ── 결과 구조체 제한 규약 ──────────────────────────────────

    [Fact]
    public void Default_result_is_not_established_and_surfaces_as_internal_error()
    {
        SecureChannelResult result = default;

        Assert.Equal(SecureChannelStatus.None, result.Status);
        Assert.False(result.IsEstablished);
        Assert.Null(result.Channel);
        // 센티넬이 흘러가면 조용히 지나가지 않고 Internal 로 드러난다.
        Assert.Equal(ErrorCode.Internal, result.ToErrorCode());
    }

    [Fact]
    public void Established_requires_channel()
    {
        Assert.Throws<ArgumentNullException>(static () => SecureChannelResult.Established(null!));
    }

    [Fact]
    public void Established_result_carries_channel_and_no_error()
    {
        var channel = new PassThroughChannel(new Pipe().Reader, new Pipe().Writer);

        SecureChannelResult result = SecureChannelResult.Established(channel);

        Assert.True(result.IsEstablished);
        Assert.Same(channel, result.Channel);
        Assert.Equal(ErrorCode.None, result.ToErrorCode());
    }

    [Theory]
    [InlineData(SecureChannelStatus.Established)]
    [InlineData(SecureChannelStatus.None)]
    public void Failed_rejects_non_failure_status(SecureChannelStatus status)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SecureChannelResult.Failed(status));
    }

    [Theory]
    [InlineData(SecureChannelStatus.HandshakeFailed, ErrorCode.SecureChannelFailed)]
    [InlineData(SecureChannelStatus.Canceled, ErrorCode.OperationCanceled)]
    public void Failed_result_maps_to_error_code(SecureChannelStatus status, ErrorCode expected)
    {
        SecureChannelResult result = SecureChannelResult.Failed(status);

        Assert.False(result.IsEstablished);
        Assert.Null(result.Channel);
        Assert.Equal(expected, result.ToErrorCode());
    }

    // ── 계약 구현 가능성(벤더 의존 0) ──────────────────────────

    [Fact]
    public async Task PassThrough_implementation_roundtrips_bytes_through_channel()
    {
        // 전송 쌍: 클라→서버 파이프 하나, 서버→클라 파이프 하나.
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var serverSide = new DuplexPipe(clientToServer.Reader, serverToClient.Writer);

        ITransportSecurity security = new PassThroughTransportSecurity();
        SecureChannelResult result = await security.SecureAsServerAsync(serverSide, CancellationToken.None);

        Assert.True(result.IsEstablished);
        ISecureChannel channel = result.Channel!;

        byte[] sent = [1, 2, 3, 4];
        await clientToServer.Writer.WriteAsync(sent);

        ReadResult read = await channel.Input.ReadAsync();
        Assert.Equal(sent, read.Buffer.ToArray());
        channel.Input.AdvanceTo(read.Buffer.End);

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Canceled_handshake_reports_status_instead_of_throwing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        ITransportSecurity security = new PassThroughTransportSecurity();
        var pipe = new Pipe();
        SecureChannelResult result = await security.SecureAsClientAsync(
            new DuplexPipe(pipe.Reader, pipe.Writer), cts.Token);

        // 계약: 취소는 예외가 아니라 상태로 보고된다.
        Assert.Equal(SecureChannelStatus.Canceled, result.Status);
    }

    /// <summary>양방향 파이프 최소 구현.</summary>
    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }

    /// <summary>암호화 없이 원본 파이프를 그대로 노출하는 채널 — 계약 구현 가능성 증명용.</summary>
    private sealed class PassThroughChannel(PipeReader input, PipeWriter output) : ISecureChannel
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>패스스루 보안 축 — "없음" 조립에 해당하는 최소 구현.</summary>
    private sealed class PassThroughTransportSecurity : ITransportSecurity
    {
        public ValueTask<SecureChannelResult> SecureAsServerAsync(IDuplexPipe transport, CancellationToken cancellationToken) =>
            Secure(transport, cancellationToken);

        public ValueTask<SecureChannelResult> SecureAsClientAsync(IDuplexPipe transport, CancellationToken cancellationToken) =>
            Secure(transport, cancellationToken);

        private static ValueTask<SecureChannelResult> Secure(IDuplexPipe transport, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(SecureChannelResult.Failed(SecureChannelStatus.Canceled));
            }

            return ValueTask.FromResult(
                SecureChannelResult.Established(new PassThroughChannel(transport.Input, transport.Output)));
        }
    }
}
