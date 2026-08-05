using System;
using System.Buffers;
using MemoryPack;

namespace ChServerM.Serialization.MemoryPack;

/// <summary>
/// MemoryPack 으로 메시지를 직렬화·역직렬화한다.
/// </summary>
/// <typeparam name="TMessage">다루는 메시지 타입. <c>[MemoryPackable]</c> 이거나
/// MemoryPack 이 기본 지원하는 타입(문자열·컬렉션 등)이어야 한다.</typeparam>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 직렬화 축(<see cref="IMessageSerializer{TMessage}"/>)의 첫 실동
/// 어댑터다(ADR-0011). C#↔C# 최속 가설(CLAUDE.md 6절)의 검증 대상이며, 소스 제너레이터
/// 기반이라 "리플렉션 대신 소스 제너레이터" 하드 룰과 Native AOT 배포에 그대로 부합한다.
/// 이 어셈블리가 없어도 Core 는 컴파일된다 — 벤더 타입은 여기 밖으로 나가지 않는다.
/// </para>
/// <para>
/// <b>제로 카피 규약.</b> 쓰기는 <see cref="IBufferWriter{T}"/>, 읽기는
/// <see cref="ReadOnlySequence{T}"/> 를 MemoryPack 네이티브 오버로드에 직결한다.
/// 중간 배열·평탄화가 없다 — 레거시가 <c>ToArray()</c> 로 제로 카피를 무너뜨린
/// 결함(IMessageSerializer 계약 주석 참조)이 구조적으로 재발할 수 없다.
/// </para>
/// <para>
/// <b>엄격 소비 규약.</b> 역직렬화가 페이로드를 <b>끝까지 소비하지 않으면 실패</b>로
/// 판정한다. 프레이밍이 페이로드 길이를 정확히 알려주므로 잔여 바이트는 스키마 불일치
/// 또는 조작된 입력이다 — 뒤에 데이터를 숨겨 보내는 경로를 여기서 끊는다.
/// </para>
/// <para>
/// <b><see langword="null"/> 메시지는 존재하지 않는다.</b> 인코더는
/// <see langword="null"/> 을 예외로 거부하고, 디코더는 <see langword="null"/> 로 풀리는
/// 페이로드를 실패로 판정한다. 조용히 통과시키면 핸들러가
/// <see cref="NullReferenceException"/> 으로 죽는 시점이 뒤로 밀릴 뿐이다 —
/// "조용한 유실 대신 예외"(ADR-0010)와 같은 원칙이다.
/// </para>
/// <para><b>스레드 규약.</b> 상태가 없어 스레드 안전하다. 타입당 하나면 충분하며
/// <see cref="MemoryPackMessageSerializerProvider"/> 가 그 캐시를 맡는다 —
/// 커넥션마다 인스턴스를 두지 않는다.</para>
/// </remarks>
public sealed class MemoryPackMessageSerializer<TMessage> : IMessageSerializer<TMessage>
{
    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> 또는
    /// <paramref name="message"/> 가 <see langword="null"/> 일 때. null 메시지 거부는
    /// 계약이다 — 모듈 주석 참조.</exception>
    /// <exception cref="MemoryPackSerializationException"><typeparamref name="TMessage"/> 의
    /// 포매터가 등록돼 있지 않을 때. 조립 오류이므로 예외가 옳다.</exception>
    public void Serialize(IBufferWriter<byte> writer, in TMessage message)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // ThrowIfNull(object?) 는 struct TMessage 를 박싱한다. 제네릭 null 검사는
        // 값 타입에서 JIT 가 통째로 제거하므로 이 형태가 핫패스에서 공짜다.
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        MemoryPackSerializer.Serialize(writer, message);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// IL2091 억제 근거: MemoryPack 의 <c>Deserialize&lt;T&gt;</c> 가 요구하는
    /// <c>DynamicallyAccessedMembers</c> 는 포매터 미등록 타입의 리플렉션 폴백용이다.
    /// 이 프레임워크는 소스 제너레이터 생성 포매터만 쓰므로("리플렉션 금지" 하드 룰)
    /// 그 경로가 실행되지 않는다 — AOT 검증(CI)이 이 전제를 지킨다.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2091",
        Justification = "소스 제너레이터 생성 포매터만 사용한다. 리플렉션 폴백 경로는 실행되지 않는다.")]
    public bool TryDeserialize(in ReadOnlySequence<byte> payload, out TMessage message)
    {
        TMessage? value = default;
        int consumed;

        try
        {
            consumed = MemoryPackSerializer.Deserialize(payload, ref value);
        }
        catch (MemoryPackSerializationException)
        {
            // 손상된 페이로드는 정상적인 입력의 일부다(버그이거나 공격이다).
            // 예외를 밖으로 흘리면 그 자체가 서비스 거부 경로가 된다 — 계약대로 false.
            message = default!;
            return false;
        }

        // 잔여 바이트 = 스키마 불일치 또는 조작. null 로 풀린 페이로드도 메시지가 아니다.
        if (consumed != payload.Length || value is null)
        {
            message = default!;
            return false;
        }

        message = value;
        return true;
    }
}
