namespace ChServerM.RealTime.Spatial;

/// <summary>
/// 모튼 코드(Z-order curve) — 2D 좌표를 공간 지역성이 보존되는 1차원 키로 바꾼다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 가까운 좌표가 가까운 키가 되므로, 정렬된 배열·B-트리로 범위 질의를
/// 하거나 그리드 셀 키로 쓸 수 있다. 폐기된 레거시 QuadGrid 작업에서 <b>유일하게 살아남은
/// 실제 구현</b>(<c>MortonCodeM</c>)의 승계다 — 비트 트릭 자체는 정석이라 그대로 가져왔다.
/// </para>
/// <para>
/// <b>막는 레거시 결함.</b> 원본 <c>MortonIndex2</c>는 정규화 나눗셈에 0 나누기 방어가 없고,
/// 범위 밖 좌표를 마스크가 <b>조용히 잘라내</b> 엉뚱한 셀로 매핑했다. 이 타입은 정규화를
/// 하지 않는다 — 좌표를 격자 인덱스로 바꾸는 책임(클램프 포함)은 <see cref="InterestGrid"/>가
/// 지고, 여기는 이미 유효한 16비트 인덱스만 받는다(타입이 강제한다).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 순수 함수만 있다. 어디서든 호출해도 된다.
/// </para>
/// </remarks>
public static class MortonCode
{
    /// <summary>2D 격자 인덱스를 모튼 키로 인코딩한다.</summary>
    /// <param name="x">X 인덱스. <see langword="ushort"/>라 범위 초과가 컴파일 타임에 막힌다.</param>
    /// <param name="y">Y 인덱스.</param>
    /// <returns>비트가 교차 배치된 32비트 키. 가까운 (x, y)는 가까운 키가 된다.</returns>
    public static uint Encode(ushort x, ushort y) => (Part1By1(y) << 1) | Part1By1(x);

    /// <summary>모튼 키를 2D 격자 인덱스로 복원한다. <see cref="Encode"/>의 역이다.</summary>
    public static (ushort X, ushort Y) Decode(uint code) =>
        ((ushort)Compact1By1(code), (ushort)Compact1By1(code >> 1));

    /// <summary>하위 16비트를 32비트에 한 칸씩 벌려 배치한다 (…-b3-b2-b1-b0 → …0b3-0b2-0b1-0b0).</summary>
    private static uint Part1By1(uint value)
    {
        value &= 0x0000ffff;
        value = (value ^ (value << 8)) & 0x00ff00ff;
        value = (value ^ (value << 4)) & 0x0f0f0f0f;
        value = (value ^ (value << 2)) & 0x33333333;
        value = (value ^ (value << 1)) & 0x55555555;
        return value;
    }

    /// <summary><see cref="Part1By1"/>의 역 — 짝수 비트를 하위 16비트로 모은다.</summary>
    private static uint Compact1By1(uint value)
    {
        value &= 0x55555555;
        value = (value ^ (value >> 1)) & 0x33333333;
        value = (value ^ (value >> 2)) & 0x0f0f0f0f;
        value = (value ^ (value >> 4)) & 0x00ff00ff;
        value = (value ^ (value >> 8)) & 0x0000ffff;
        return value;
    }
}
