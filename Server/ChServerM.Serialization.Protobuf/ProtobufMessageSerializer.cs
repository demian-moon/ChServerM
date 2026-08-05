using System;
using System.Buffers;
using Google.Protobuf;

namespace ChServerM.Serialization.Protobuf;

/// <summary>
/// Google.Protobuf 로 메시지를 직렬화·역직렬화한다.
/// </summary>
/// <typeparam name="TMessage">protoc 가 생성한 메시지 타입.</typeparam>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 직렬화 축의 두 번째 실동 어댑터다(ADR-0012). MemoryPack 과 성질이
/// 정반대다 — 스키마 파일(.proto), 필드 태그, varint 가변 인코딩, 크로스 언어 —
/// 그래서 이 어댑터가 컴파일되고 같은 핸들러가 돈다는 사실 자체가
/// <see cref="IMessageSerializer{TMessage}"/> 계약의 중립성 증명이다(DoD-5).
/// 크로스 언어 클라이언트 요구가 생겼을 때의 탈출구이기도 하다.
/// </para>
/// <para>
/// <b>제로 카피 규약.</b> 쓰기는 <c>WriteTo(IBufferWriter)</c>, 읽기는
/// <c>ParseFrom(ReadOnlySequence)</c> 네이티브 오버로드에 직결한다. 중간 배열이 없다.
/// </para>
/// <para>
/// <b>포맷 특성 — 관대한 파서.</b> proto3 는 모르는 필드를 보존하고(스키마 진화의
/// 근거), 우연히 유효한 태그로 읽히는 바이트열을 성공으로 판정할 수 있다.
/// MemoryPack 어댑터의 엄격 소비와 달리 "임의 바이트 거부"를 포맷이 보장하지 않는다 —
/// 무결성은 Phase 9 AEAD 의 몫이라는 프레임워크 전제가 여기서도 성립해야 한다.
/// </para>
/// <para>
/// <b><see langword="null"/> 메시지는 존재하지 않는다.</b> 인코더는
/// <see langword="null"/> 을 예외로 거부한다(조용한 유실 금지, ADR-0010).
/// proto3 파서는 구조상 <see langword="null"/> 을 만들지 않는다.
/// </para>
/// <para><b>스레드 규약.</b> 상태가 없어 스레드 안전하다. 타입당 하나면 충분하며
/// <see cref="ProtobufMessageSerializerProvider"/> 가 그 캐시를 맡는다.</para>
/// </remarks>
public sealed class ProtobufMessageSerializer<TMessage> : IMessageSerializer<TMessage>
    where TMessage : class, IMessage<TMessage>, new()
{
    // 파서는 타입당 하나. protoc 생성 코드의 static Parser 와 동일물이지만,
    // 여기서 직접 만들면 제네릭 제약(new())만으로 충분해 리플렉션이 없다.
    private static readonly MessageParser<TMessage> Parser = new(static () => new TMessage());

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> 또는
    /// <paramref name="message"/> 가 <see langword="null"/> 일 때.</exception>
    public void Serialize(IBufferWriter<byte> writer, in TMessage message)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(message);

        message.WriteTo(writer);
    }

    /// <inheritdoc/>
    public bool TryDeserialize(in ReadOnlySequence<byte> payload, out TMessage message)
    {
        try
        {
            message = Parser.ParseFrom(payload);
            return true;
        }
        catch (InvalidProtocolBufferException)
        {
            // 손상된 페이로드는 정상적인 입력의 일부다(버그이거나 공격이다).
            // 예외를 밖으로 흘리면 그 자체가 서비스 거부 경로가 된다 — 계약대로 false.
            message = null!;
            return false;
        }
    }
}
