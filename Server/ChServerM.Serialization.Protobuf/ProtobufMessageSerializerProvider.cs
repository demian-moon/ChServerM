using System;
using System.Collections.Generic;
using Google.Protobuf;

namespace ChServerM.Serialization.Protobuf;

/// <summary>
/// 명시적으로 등록한 protobuf 메시지 타입에 직렬화기를 내주는 제공자.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유 — 그리고 왜 MemoryPack 제공자와 모양이 다른가.</b>
/// <see cref="IMessageSerializerProvider.Find{TMessage}"/> 는 제약 없는 제네릭이라,
/// <c>IMessage&lt;T&gt;</c> 제약이 걸린 <see cref="ProtobufMessageSerializer{TMessage}"/> 를
/// 그 안에서 만들려면 <c>MakeGenericType</c> 리플렉션이 필요하다 — "리플렉션 대신
/// 소스 제너레이터" 하드 룰과 Native AOT 게이트 위반이다. 그래서 이 제공자는
/// <b>조립 시점 명시 등록</b>(<see cref="Register{TMessage}"/>) 방식을 쓴다.
/// 등록 누락은 <see cref="Find{TMessage}"/> 가 <see langword="null"/> 을 돌려줘
/// 조립 오류로 드러난다 — 계약이 의도한 실패 경로다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> <see cref="Register{TMessage}"/> 는 조립 시점 단일 스레드 전용이다.
/// 등록이 끝난 뒤의 <see cref="Find{TMessage}"/> 는 읽기 전용이라 스레드 안전하다.
/// 조립 중 동시 접근은 지원하지 않는다 — 조립 비용은 시작 시점에 지불한다(ADR-0000).
/// </para>
/// </remarks>
public sealed class ProtobufMessageSerializerProvider : IMessageSerializerProvider
{
    private readonly Dictionary<Type, object> _serializers = [];

    /// <summary>메시지 타입을 등록한다.</summary>
    /// <typeparam name="TMessage">protoc 가 생성한 메시지 타입.</typeparam>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentException">같은 타입이 이미 등록돼 있을 때.
    /// 중복 등록은 조립 실수이므로 덮어쓰지 않고 실패시킨다.</exception>
    public ProtobufMessageSerializerProvider Register<TMessage>()
        where TMessage : class, IMessage<TMessage>, new()
    {
        if (!_serializers.TryAdd(typeof(TMessage), new ProtobufMessageSerializer<TMessage>()))
        {
            throw new ArgumentException($"{typeof(TMessage)} 는 이미 등록돼 있다.");
        }

        return this;
    }

    /// <inheritdoc/>
    public IMessageSerializer<TMessage>? Find<TMessage>()
        => _serializers.TryGetValue(typeof(TMessage), out object? serializer)
            ? (IMessageSerializer<TMessage>)serializer
            : null;
}
