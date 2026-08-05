using System.Runtime.CompilerServices;
using MemoryPack;

namespace ChServerM.Serialization.MemoryPack;

/// <summary>
/// MemoryPack 이 다룰 수 있는 타입에 <see cref="MemoryPackMessageSerializer{TMessage}"/> 를
/// 내주는 제공자.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 빌더에서 <c>.UseMemoryPack()</c> ↔ <c>.UseProtobuf()</c> 로 축을
/// 갈아끼울 때 교체되는 객체가 바로 이것이다(<see cref="IMessageSerializerProvider"/> 계약).
/// 조회는 조립 시점에 끝난다 — 디스패처가 핸들러 등록 때 한 번 찾아 테이블에 박아두므로
/// 이 조회가 핫패스에 들어오지 않는다.
/// </para>
/// <para>
/// <b>판정 규약.</b> <see cref="Find{TMessage}"/> 는 MemoryPack 포매터 등록 여부로
/// 판정한다. <c>[MemoryPackable]</c> 생성 코드는 <b>해당 타입의 정적 생성자</b>에서
/// 포매터를 등록하는데, <c>IsRegistered</c> 는 수동 조회라 그 실행을 보장하지 않는다 —
/// 한 번도 만진 적 없는 타입이 "미등록"으로 오판되는 거짓 음성이 실제로 재현됐다.
/// 그래서 미등록 판정 전에 정적 생성자를 명시적으로 실행한다(테스트로 고정).
/// 그래도 미등록이면 <see langword="null"/> — 계약대로 조립 오류로 올리는 것은 호출자 몫이다.
/// </para>
/// <para><b>스레드 규약.</b> 상태가 없어 스레드 안전하다.</para>
/// </remarks>
public sealed class MemoryPackMessageSerializerProvider : IMessageSerializerProvider
{
    /// <summary>공유 인스턴스. 상태가 없으므로 이것 하나면 된다.</summary>
    public static MemoryPackMessageSerializerProvider Instance { get; } = new();

    private MemoryPackMessageSerializerProvider()
    {
    }

    /// <inheritdoc/>
    /// <remarks>
    /// IL2059 억제 근거: <typeparamref name="TMessage"/> 는 핸들러 등록 지점에서 항상
    /// 구체 타입으로 닫히고, 그 타입의 <c>[MemoryPackable]</c> 생성 코드는
    /// <c>[Preserve]</c> 로 정적 생성자까지 보존된다. 트리머가 이를 증명하지 못할 뿐이다 —
    /// AOT 검증(CI)이 이 전제를 지킨다.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2059",
        Justification = "TMessage 는 등록 지점에서 구체 타입으로 닫히고 생성 코드가 정적 생성자를 보존한다.")]
    public IMessageSerializer<TMessage>? Find<TMessage>()
    {
        if (!MemoryPackFormatterProvider.IsRegistered<TMessage>())
        {
            // [MemoryPackable] 생성 코드의 포매터 등록은 타입 정적 생성자에 있다.
            // 실행을 강제해 "아직 안 만진 타입 = 미등록" 오판을 막는다. 조립 시점 1회 경로라
            // 비용은 문제가 아니다. 정적 생성자가 없는 타입(비대상)에는 아무 일도 없다.
            RuntimeHelpers.RunClassConstructor(typeof(TMessage).TypeHandle);

            if (!MemoryPackFormatterProvider.IsRegistered<TMessage>())
            {
                return null;
            }
        }

        return Cache<TMessage>.Instance;
    }

    /// <summary>
    /// 타입당 직렬화기 캐시. 제네릭 타입의 public 정적 멤버(CA1000)를 피하면서
    /// "조회할 때마다 같은 인스턴스"를 보장한다.
    /// </summary>
    private static class Cache<TMessage>
    {
        internal static readonly MemoryPackMessageSerializer<TMessage> Instance = new();
    }
}
