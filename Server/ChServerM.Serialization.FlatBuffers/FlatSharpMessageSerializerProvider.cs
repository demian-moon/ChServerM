using System;
using System.Collections.Generic;
using FlatSharp;

namespace ChServerM.Serialization.FlatBuffers;

/// <summary>
/// 명시적으로 등록한 FlatSharp 테이블 타입에 직렬화기를 내주는 제공자.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> Protobuf 제공자와 같은 명시 등록 방식이다 — FlatSharp 생성
/// 직렬화기(<c>TMessage.Serializer</c>)는 타입별 정적 멤버라, 제약 없는
/// <see cref="IMessageSerializerProvider.Find{TMessage}"/> 안에서 리플렉션 없이
/// 얻을 방법이 없다. 등록 시점에 실물을 받는 것이 AOT 호환의 유일한 경로다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> <see cref="Register{TMessage}"/> 는 조립 시점 단일 스레드 전용,
/// 등록 완료 후 <see cref="Find{TMessage}"/> 는 읽기 전용이라 스레드 안전하다.
/// </para>
/// </remarks>
public sealed class FlatSharpMessageSerializerProvider : IMessageSerializerProvider
{
    private readonly Dictionary<Type, object> _serializers = [];

    /// <summary>테이블 타입과 그 생성 직렬화기를 등록한다.</summary>
    /// <typeparam name="TMessage">FlatSharp.Compiler 가 생성한 테이블 타입.</typeparam>
    /// <param name="serializer">생성 직렬화기 (예: <c>TMessage.Serializer</c>).
    /// Greedy 계열이어야 한다 — <see cref="FlatSharpMessageSerializer{TMessage}"/> 참조.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentException">같은 타입이 이미 등록돼 있거나
    /// 역직렬화 옵션이 Greedy 계열이 아닐 때.</exception>
    public FlatSharpMessageSerializerProvider Register<TMessage>(ISerializer<TMessage> serializer)
        where TMessage : class
    {
        if (!_serializers.TryAdd(typeof(TMessage), new FlatSharpMessageSerializer<TMessage>(serializer)))
        {
            throw new ArgumentException(
                $"{typeof(TMessage)} 는 이미 등록돼 있다. 제공자는 타입당 직렬화기 하나만 갖는다(교체 API 없음) — "
                + "중복 Register 호출 지점을 찾아 하나로 합친다.", nameof(serializer));
        }

        return this;
    }

    /// <inheritdoc/>
    public IMessageSerializer<TMessage>? Find<TMessage>()
        => _serializers.TryGetValue(typeof(TMessage), out object? serializer)
            ? (IMessageSerializer<TMessage>)serializer
            : null;
}
