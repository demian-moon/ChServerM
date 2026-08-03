using System;
using System.IO.Pipelines;
using System.Threading;
using ChServerM.Features;
using ChServerM.Identity;

namespace ChServerM.Connections;

/// <summary>
/// 전송 종류와 무관한 양방향 바이트 채널 하나.
/// </summary>
/// <remarks>
/// <para>
/// <b>이 인터페이스가 좁게 유지되는 것이 프레임워크의 교체 가능성 그 자체다.</b>
/// TCP·인메모리·WebSocket·QUIC이 모두 여기에 들어와야 한다. 전송별로만 의미 있는 것
/// (원격 주소, TLS 정보, keep-alive, 짝 커넥션)은 전부 <see cref="Features"/>로 간다.
/// </para>
/// <para>
/// 바이트 경로는 <see cref="PipeReader"/>/<see cref="PipeWriter"/>다(ADR-0006).
/// 백프레셔·버퍼 재사용·부분 읽기가 이미 이 타입의 계약에 들어 있어서,
/// 전송마다 다시 만들 필요가 없다.
/// </para>
/// <para>
/// <b>수명.</b> 정상 종료는 <see cref="IAsyncDisposable.DisposeAsync"/>,
/// 즉시 중단은 <see cref="Abort"/>다. 둘 다 여러 번 호출해도 안전해야 한다.
/// </para>
/// <para>
/// <b>스레드 사용 규약.</b> <see cref="Input"/>은 읽기 루프 하나가,
/// <see cref="Output"/>은 쓰기 경로 하나가 소유한다.
/// <see cref="PipeWriter"/>는 동시 쓰기를 허용하지 않으므로 다중 생산자가 필요하면
/// 상위에서 직렬화한다 — <b>공유하지 않는 것이 1순위</b>(CLAUDE.md 9장).
/// </para>
/// </remarks>
public interface IConnection : IAsyncDisposable
{
    /// <summary>이 커넥션의 핸들.</summary>
    /// <remarks>슬롯이 재사용돼도 세대가 달라 낡은 핸들과 구분된다.</remarks>
    ConnectionId Id { get; }

    /// <summary>수신 바이트 스트림.</summary>
    PipeReader Input { get; }

    /// <summary>송신 바이트 스트림.</summary>
    PipeWriter Output { get; }

    /// <summary>전송별 선택 기능.</summary>
    IFeatureCollection Features { get; }

    /// <summary>커넥션이 닫힐 때 취소되는 토큰.</summary>
    /// <remarks>
    /// 이 커넥션에 매인 모든 비동기 작업이 이 토큰을 받아야 한다.
    /// 레거시는 종료 시 진행 중인 작업을 남겨둬 이미 닫힌 소켓에 쓰기를 시도했다.
    /// </remarks>
    CancellationToken ConnectionClosed { get; }

    /// <summary>커넥션을 즉시 중단한다.</summary>
    /// <param name="info">중단 이유.</param>
    /// <remarks>
    /// <para>
    /// 대기 중인 송신 데이터를 <b>보장하지 않는다.</b> 남은 데이터를 내보내야 하면
    /// <see cref="IAsyncDisposable.DisposeAsync"/>를 쓴다.
    /// </para>
    /// <para>이미 닫힌 커넥션에 호출해도 예외를 던지지 않는다.</para>
    /// </remarks>
    void Abort(in ConnectionCloseInfo info);
}
