using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

namespace ChServerM.Security.Tls;

/// <summary>
/// 핸드셰이크가 끝난 <c>SslStream</c>을 <see cref="ISecureChannel"/>로 노출하는 채널.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Input"/>/<see cref="Output"/>은 평문 측이다 — BCL 의
/// <c>PipeReader.Create(Stream)</c>/<c>PipeWriter.Create(Stream)</c>으로 만든다.
/// 직접 펌프 루프를 두지 않는 이유: 검증된 BCL 구현을 두고 재작성하면
/// 그것이 다시 감사 대상이 된다(ADR-0017 의 원칙과 동일).
/// </para>
/// <para>
/// <b>수명.</b> <see cref="DisposeAsync"/> 순서 — 남은 평문 flush(Output 완결) →
/// close_notify 최선 노력 → <c>SslStream</c> 폐기(내부 브리지를 거쳐 원본 파이프
/// 완결 전파). 원본 전송의 수명은 커넥션이 소유한다(<see cref="ISecureChannel"/> 계약).
/// 여러 번 폐기해도 안전하다.
/// </para>
/// </remarks>
internal sealed class TlsSecureChannel : ISecureChannel
{
    private readonly SslStream _ssl;
    private int _disposed;

    public TlsSecureChannel(SslStream ssl)
    {
        _ssl = ssl;
        // leaveOpen: true — 스트림 폐기는 DisposeAsync 가 순서를 제어한다.
        Input = PipeReader.Create(ssl, new StreamPipeReaderOptions(leaveOpen: true));
        Output = PipeWriter.Create(ssl, new StreamPipeWriterOptions(leaveOpen: true));
    }

    public PipeReader Input { get; }

    public PipeWriter Output { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            // 완결이 버퍼에 남은 평문을 flush 한다. 상대가 이미 사라졌으면
            // 실패하지만, 폐기 경로에서는 보낼 곳이 없는 데이터일 뿐이다.
            await Output.CompleteAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }

        await Input.CompleteAsync().ConfigureAwait(false);

        try
        {
            // close_notify — 상대가 "잘림"과 "정상 종료"를 구분하게 한다. 최선 노력.
            await _ssl.ShutdownAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        // leaveInnerStreamOpen: false 로 만들었으므로 내부 브리지까지 폐기되고,
        // 브리지가 원본 파이프에 완결을 전파한다.
        await _ssl.DisposeAsync().ConfigureAwait(false);
    }
}
