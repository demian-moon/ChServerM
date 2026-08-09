using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;

namespace ChServerM.Bench.LoadRunner;

/// <summary>
/// <b>비교 바닥선</b> — ChServerM 코드를 하나도 쓰지 않고 같은 와이어 포맷으로 에코하는
/// 최소 Kestrel 서버.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 프레임워크는 자기 가격표를 알아야 한다.</b> 지금까지의 측정은 전부
/// ChServerM 안에서의 상대 비교였다(전송 A/B, 직렬화 4자, 코어 확장성). 그것으로는
/// "우리가 빠른가" 가 아니라 "우리끼리 어느 쪽이 빠른가" 밖에 답할 수 없다. 이 서버는
/// <b>같은 소켓 엔진(Kestrel <c>SocketTransportFactory</c>) 위에서 프레임워크를 전부
/// 걷어낸 것</b>이며, 그 차이가 곧 <b>조립 가능성·순서 보장·관측성의 가격표</b>다.
/// </para>
/// <para>
/// <b>무엇을 걷어냈나.</b> 프레이밍 계약(<c>IFrameDecoder</c>)·디스패치·미들웨어
/// 파이프라인·파티션 실행 모델·커넥션 레지스트리·메트릭·수용 제어가 전부 없다.
/// 남은 것은 <c>PipeReader</c> 에서 16바이트 헤더를 직접 읽고 프레임을 통째로 되쓰는
/// 루프 하나뿐이다.
/// </para>
/// <para>
/// <b>⚠ 이 비교는 의도적으로 바닥선 쪽에 유리하게 기울어 있다.</b> 세 가지가 그렇다:
/// </para>
/// <list type="number">
///   <item>에코가 <b>받은 바이트를 그대로 되쓴다</b> — ChServerM 은 헤더를 다시 인코딩한다.
///   되쓰기가 더 싸다</item>
///   <item>헤더 검증이 <b>페이로드 길이 상한 하나뿐</b>이다 — 버전·플래그·예약 필드를
///   보지 않는다</item>
///   <item>커넥션 수 상한·idle 스윕·거부 통지·수명 이벤트가 없다. 즉 <b>운영에 필요한
///   기능이 없다</b></item>
/// </list>
/// <para>
/// 따라서 여기서 나오는 차이는 <b>프레임워크 세금의 상한</b>으로 읽어야 한다 —
/// "이보다 나쁘지는 않다". 바닥선을 우리에게 유리하게 만들면 비교가 자기 위안이 된다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 커넥션당 하나의 <see cref="ConnectionContext"/> 처리 태스크가 돌고,
/// 그 안에서만 해당 파이프를 만진다. 공유 상태가 없다(그래서 순서 보장·파티셔닝도 없다 —
/// 그것이 ChServerM 이 더 하는 일이다).
/// </para>
/// </remarks>
internal static class RawKestrelEchoServer
{
    private const int HeaderSize = 16;
    private const int PayloadLengthOffset = 4;

    /// <summary>바닥선 에코 서버를 지정한 시간 동안 돌린다.</summary>
    /// <param name="endPoint">바인드 종단.</param>
    /// <param name="maxPayloadLength">페이로드 길이 상한 — 유일한 검증이다.</param>
    /// <param name="seconds">실행 시간(초).</param>
    public static async Task RunAsync(IPEndPoint endPoint, int maxPayloadLength, int seconds)
    {
        SocketTransportOptions options = new();
        SocketTransportFactory factory = new(Options.Create(options), NullLoggerFactory.Instance);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(seconds));
        IConnectionListener listener = await factory.BindAsync(endPoint, stop.Token).ConfigureAwait(false);

        Console.WriteLine($"raw Kestrel 바닥선 — {listener.EndPoint} (프레임워크 없음, {seconds}s)");

        try
        {
            while (!stop.IsCancellationRequested)
            {
                ConnectionContext? connection = await listener.AcceptAsync(stop.Token).ConfigureAwait(false);
                if (connection is null)
                {
                    break;
                }

                // 수락 루프를 막지 않는다. 실패는 커넥션 하나로 격리한다.
                _ = Task.Run(() => EchoLoopAsync(connection, maxPayloadLength), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // 시간 종료.
        }
        finally
        {
            await listener.UnbindAsync(CancellationToken.None).ConfigureAwait(false);
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>한 커넥션의 수신→에코 루프. 프레임 경계만 보고 바이트를 그대로 되쓴다.</summary>
    private static async Task EchoLoopAsync(ConnectionContext connection, int maxPayloadLength)
    {
        try
        {
            while (true)
            {
                ReadResult read = await connection.Transport.Input.ReadAsync().ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = read.Buffer;

                SequencePosition consumed = buffer.Start;
                while (TryReadFrame(ref buffer, maxPayloadLength, out ReadOnlySequence<byte> frame))
                {
                    // ⚠ 받은 바이트를 그대로 되쓴다 — 헤더를 다시 만들지 않는다.
                    // ChServerM 보다 싼 경로이며, 그 편향은 타입 문서에 명시했다.
                    foreach (ReadOnlyMemory<byte> segment in frame)
                    {
                        connection.Transport.Output.Write(segment.Span);
                    }

                    consumed = buffer.Start;
                }

                connection.Transport.Input.AdvanceTo(consumed, read.Buffer.End);

                if (!connection.Transport.Output.CanGetUnflushedBytes || connection.Transport.Output.UnflushedBytes > 0)
                {
                    await connection.Transport.Output.FlushAsync().ConfigureAwait(false);
                }

                if (read.IsCompleted)
                {
                    break;
                }
            }
        }
#pragma warning disable CA1031 // 바닥선 — 커넥션 하나의 실패를 격리하는 것 외에 할 일이 없다.
        catch (Exception)
        {
            // 클라이언트 종료 등. 측정에서 오류 수는 클라이언트가 센다.
        }
#pragma warning restore CA1031
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>완전한 프레임 하나를 떼어낸다. 검증은 페이로드 길이 상한 하나뿐이다.</summary>
    private static bool TryReadFrame(
        ref ReadOnlySequence<byte> buffer, int maxPayloadLength, out ReadOnlySequence<byte> frame)
    {
        frame = default;

        if (buffer.Length < HeaderSize)
        {
            return false;
        }

        // 헤더는 세그먼트를 걸칠 수 있다. 첫 세그먼트에 다 있으면 복사 없이 읽는다.
        // 길이 하나만 꺼내고 끝내므로 스크래치를 밖으로 흘리지 않는다(CS8352 회피이자
        // 애초에 필요한 것이 그것뿐이다).
        uint payloadLength;
        ReadOnlySpan<byte> first = buffer.FirstSpan;
        if (first.Length >= HeaderSize)
        {
            payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(first[PayloadLengthOffset..]);
        }
        else
        {
            Span<byte> headerScratch = stackalloc byte[HeaderSize];
            buffer.Slice(0, HeaderSize).CopyTo(headerScratch);
            payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(headerScratch[PayloadLengthOffset..]);
        }

        if (payloadLength > (uint)maxPayloadLength)
        {
            throw new InvalidOperationException($"페이로드 길이 상한 초과: {payloadLength}");
        }

        long total = HeaderSize + (long)payloadLength;
        if (buffer.Length < total)
        {
            return false;
        }

        frame = buffer.Slice(0, total);
        buffer = buffer.Slice(total);
        return true;
    }
}
