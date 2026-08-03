using System.Buffers;

namespace ChServerM.Serialization;

/// <summary>
/// 메시지 하나를 바이트로, 바이트를 메시지로 바꾼다.
/// </summary>
/// <typeparam name="TMessage">다루는 메시지 타입.</typeparam>
/// <remarks>
/// <para>
/// <b>이 인터페이스가 직렬화 축의 교체점이다.</b> FlatBuffers·Protobuf·MessagePack·
/// 수제 코덱이 전부 여기로 들어온다. 프레이밍 헤더에는 포맷 정보가 없으므로(ADR-0002)
/// 양쪽이 같은 구현을 쓴다는 것은 <b>조립 시점의 합의</b>다.
/// </para>
/// <para>
/// 페이로드가 <see cref="ReadOnlySequence{T}"/>인 이유는 <c>PipeReader</c>가 주는 버퍼가
/// 연속 메모리라는 보장이 없기 때문이다. 여기서 <c>ToArray()</c>를 부르면
/// <b>제로 카피 경로가 통째로 무너진다</b> — 레거시가 정확히 그렇게 했다.
/// </para>
/// <para>구현체는 <b>스레드 안전해야 한다.</b> 커넥션마다 인스턴스를 두지 않는다.</para>
/// </remarks>
public interface IMessageSerializer<TMessage>
{
    /// <summary>메시지를 바이트로 쓴다.</summary>
    /// <param name="writer">출력 버퍼.</param>
    /// <param name="message">쓸 메시지.</param>
    void Serialize(IBufferWriter<byte> writer, in TMessage message);

    /// <summary>바이트에서 메시지를 읽는다.</summary>
    /// <param name="payload">프레임 페이로드.</param>
    /// <param name="message">성공하면 읽어낸 메시지.</param>
    /// <returns>읽어냈으면 <see langword="true"/>.</returns>
    /// <remarks>
    /// <para>
    /// <b>실패에 예외를 쓰지 않는다.</b> 손상된 페이로드는 정상적인 입력의 일부다
    /// (버그이거나 공격이다). 핫패스에서 예외를 던지면 그것 자체가 서비스 거부 경로가 된다.
    /// </para>
    /// <para>
    /// <paramref name="payload"/>는 이 호출이 끝나면 무효다. 참조를 들고 있는 메시지를
    /// 만들려면 <b>이 안에서</b> 복사해야 한다.
    /// </para>
    /// </remarks>
    bool TryDeserialize(in ReadOnlySequence<byte> payload, out TMessage message);
}
