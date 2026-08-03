namespace ChServerM.Serialization;

/// <summary>
/// 메시지 타입에 맞는 직렬화기를 찾아준다.
/// </summary>
/// <remarks>
/// <para>
/// 빌더에서 <c>.UseFlatBuffers()</c> ↔ <c>.UseProtobuf()</c>를 바꾸면 교체되는 것이
/// <b>바로 이 객체 하나</b>다. 디스패치·프레이밍·전송 코드는 손대지 않는다.
/// </para>
/// <para>
/// <b>조회는 조립 시점에 끝낸다.</b> 프레임마다 여기를 찌르면 딕셔너리 조회 비용이
/// 핫패스에 들어온다. 디스패처는 핸들러를 등록할 때 직렬화기를 한 번 찾아
/// 자기 테이블에 박아둔다.
/// </para>
/// <para>구현체는 <b>스레드 안전해야 한다.</b></para>
/// </remarks>
public interface IMessageSerializerProvider
{
    /// <summary>메시지 타입에 등록된 직렬화기를 찾는다.</summary>
    /// <typeparam name="TMessage">찾을 메시지 타입.</typeparam>
    /// <returns>등록돼 있으면 직렬화기, 없으면 <see langword="null"/>.</returns>
    /// <remarks>
    /// <see langword="null"/> 반환은 <b>조립 오류</b>다. 조립 시점에 발견되면 예외로 올리고,
    /// 런타임에 발견되면 <c>SerializerNotRegistered</c>로 커넥션을 닫는다.
    /// </remarks>
    IMessageSerializer<TMessage>? Find<TMessage>();
}
