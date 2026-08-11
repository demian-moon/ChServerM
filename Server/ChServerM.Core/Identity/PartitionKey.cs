using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ChServerM.Identity;

/// <summary>
/// 실행 파티션을 고르는 데 쓰는 안정 해시 키.
/// </summary>
/// <remarks>
/// <para>
/// 같은 키는 언제나 같은 파티션으로 간다. 이것이 <b>락 없이 순서를 보장하는 방식</b>이다 —
/// 같은 유저의 작업이 항상 같은 단일 소비자에게 가므로 동기화가 필요 없고,
/// 다른 키는 완전히 독립이므로 코어 수만큼 병렬로 확장된다 (ADR-0005).
/// </para>
/// <para>
/// 해시는 <b>피보나치 해싱</b>이다. 2⁶⁴/φ를 곱하고 상위 비트를 취한다 —
/// 곱셈 하나와 시프트 하나로 끝나고 하위 비트가 몰려 있는 순차 ID를 잘 흩뜨린다.
/// </para>
/// <para>
/// 레거시는 <c>oid % n</c>을 썼다. 음수 ID면 결과도 음수라 배열 인덱스가 되지 못하고,
/// <c>n</c>이 0이면 <see cref="DivideByZeroException"/>이 나며, 순차 ID가 특정 샤드에 몰린다.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct PartitionKey : IEquatable<PartitionKey>
{
    /// <summary>2⁶⁴ / φ. 황금비 역수를 64비트로 스케일한 값.</summary>
    private const ulong GoldenRatio64 = 0x9E37_79B9_7F4A_7C15UL;

    private readonly ulong _hash;

    private PartitionKey(ulong hash) => _hash = hash;

    /// <summary>임의의 64비트 식별자에서 파티션 키를 만든다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PartitionKey FromValue(ulong value) => new(unchecked(value * GoldenRatio64));

    /// <summary>이미 잘 분포된 해시값을 그대로 파티션 키로 쓴다.</summary>
    /// <remarks>입력이 균등 분포임이 확실할 때만 쓴다. 순차 ID라면 <see cref="FromValue"/>를 써야 한다.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PartitionKey FromPrecomputedHash(ulong hash) => new(hash);

    /// <summary>파티션 개수로 나눈 인덱스를 구한다.</summary>
    /// <param name="partitionCount">1 이상이어야 한다.</param>
    /// <returns><c>0</c> 이상 <paramref name="partitionCount"/> 미만의 인덱스.</returns>
    /// <remarks>
    /// 상위 비트를 쓴다. 피보나치 해싱은 상위 비트에 엔트로피가 몰리므로 나머지 연산보다 분포가 낫고,
    /// 곱셈-시프트로 나눗셈을 피한다. <paramref name="partitionCount"/>가 2의 거듭제곱일 필요는 없다.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ToIndex(int partitionCount)
    {
        // (hash >> 32) * count >> 32 — 상위 32비트를 [0, count) 로 축소한다. 나눗셈 없음.
        ulong high = _hash >> 32;
        return (int)((high * (ulong)partitionCount) >> 32);
    }

    /// <summary>원본 해시값.</summary>
    public ulong Value => _hash;

    /// <inheritdoc />
    public bool Equals(PartitionKey other) => _hash == other._hash;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PartitionKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _hash.GetHashCode();

    /// <summary>두 키가 같은지 비교한다.</summary>
    public static bool operator ==(PartitionKey left, PartitionKey right) => left.Equals(right);

    /// <summary>두 키가 다른지 비교한다.</summary>
    public static bool operator !=(PartitionKey left, PartitionKey right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"pk:{_hash:x16}");
}
