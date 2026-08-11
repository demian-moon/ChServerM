using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace ChServerM.Security.Tls;

/// <summary>
/// <see cref="IDuplexPipe"/> 위에 씌우는 양방향 <see cref="Stream"/> 브리지.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> <c>SslStream</c>이 <see cref="Stream"/> 하나를 요구하는데
/// 프레임워크의 바이트 경로는 파이프 쌍이다(ADR-0006). 변환은 BCL 의
/// <c>PipeReader.AsStream</c>/<c>PipeWriter.AsStream</c>에 위임하고, 이 타입은
/// 읽기·쓰기 스트림 두 개를 하나로 묶는 일만 한다 — 직접 펌프를 만들지 않는다.
/// </para>
/// <para>
/// <b>수명.</b> <c>leaveOpen: false</c>로 만들므로 이 스트림의 폐기가 원본 파이프의
/// 완결(Complete)로 전파된다. <c>SslStream</c>(leaveInnerStreamOpen: false) 폐기 →
/// 이 스트림 폐기 → 원본 파이프 완결 사슬이 <see cref="ISecureChannel"/>의
/// "완결 전파" 계약을 구현한다. 원본 전송(소켓)의 수명은 커넥션이 계속 소유한다.
/// </para>
/// <para><b>스레드 규약.</b> 읽기 경로 하나·쓰기 경로 하나 — 원본 파이프와 동일.</para>
/// </remarks>
internal sealed class DuplexPipeStream : Stream
{
    private readonly Stream _input;
    private readonly Stream _output;

    public DuplexPipeStream(IDuplexPipe transport)
    {
        _input = transport.Input.AsStream(leaveOpen: false);
        _output = transport.Output.AsStream(leaveOpen: false);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length =>
        throw new NotSupportedException(
            "DuplexPipeStream 은 파이프 위의 순방향 전용 스트림이라 길이가 없다. "
            + "이 예외가 보이면 SslStream 이 아닌 코드가 이 스트림을 임의 접근 스트림으로 오용한 것이다.");

    public override long Position
    {
        get => throw new NotSupportedException(
            "DuplexPipeStream 은 파이프 위의 순방향 전용 스트림이라 위치 개념이 없다. "
            + "바이트를 되돌리거나 재배치해야 한다면 PipeReader 에서 직접 읽어 버퍼링한다.");
        set => throw new NotSupportedException(
            "DuplexPipeStream 은 파이프 위의 순방향 전용 스트림이라 위치를 옮길 수 없다. "
            + "바이트를 되돌리거나 재배치해야 한다면 PipeReader 에서 직접 읽어 버퍼링한다.");
    }

    public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _input.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _input.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _input.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _output.Write(buffer);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _output.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _output.WriteAsync(buffer, cancellationToken);

    public override void Flush() => _output.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _output.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException(
            "DuplexPipeStream 은 파이프 위의 순방향 전용 스트림이라 탐색할 수 없다(CanSeek=false). "
            + "바이트를 되돌리거나 재배치해야 한다면 PipeReader 에서 직접 읽어 버퍼링한다.");

    public override void SetLength(long value) =>
        throw new NotSupportedException(
            "DuplexPipeStream 은 파이프 위의 순방향 전용 스트림이라 길이를 바꿀 수 없다. "
            + "이 예외가 보이면 SslStream 이 아닌 코드가 이 스트림을 파일처럼 오용한 것이다.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _input.Dispose();
            _output.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _input.DisposeAsync().ConfigureAwait(false);
        await _output.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
