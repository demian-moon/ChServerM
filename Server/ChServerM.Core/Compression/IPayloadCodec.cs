using System;
using System.Buffers;

namespace ChServerM.Compression;

/// <summary>
/// 페이로드 압축 축의 계약 (Phase 9, T-11·T-18).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 대역폭이 비싼 경로(모바일·대륙 간)에서 페이로드를 줄인다.
/// 압축은 <b>페이로드 수준</b> 변환이다 — 전송 보안(TLS)은 그 바깥의 바이트 스트림
/// 수준이므로 "압축 후 암호화" 순서(T-11, 역순은 CRIME 류)는 구조가 보장하고
/// 구현이 어길 방법이 없다.
/// </para>
/// <para>
/// <b>출력은 자기서술적 블롭이다.</b> <see cref="Encode"/> 가 쓰는 바이트에는 원본
/// 길이가 포함된다 — 그래야 <see cref="TryDecode"/> 가 <b>버퍼를 잡기 전에</b> 선언
/// 길이를 상한과 대조할 수 있다. 레거시는 와이어의 길이 값으로 곧장 <c>new byte[]</c>
/// 를 했고 그것이 메모리 고갈 공격 경로였다(T-12) — 여기서는 "선언값은 검증 후에만
/// 신뢰"가 계약이다.
/// </para>
/// <para>
/// <b><c>maxDecodedLength</c> 는 필수 인자다.</b> 압축 폭탄(T-18 — 작은 입력이 거대한
/// 출력으로 팽창)의 상한 검사를 구현체가 생략할 수 없게 계약 표면에 박았다.
/// 상한을 넘는 블롭은 할당 없이 실패한다.
/// </para>
/// <para>
/// <b>알고리즘은 조립 수준 합의다.</b> 와이어의 <c>Compressed</c> 플래그는 "압축됨"만
/// 말하고 알고리즘을 말하지 않는다 — 양쪽이 같은 구현체를 조립해야 하며, 불일치는
/// 해제 실패 = 커넥션 종료로 드러난다(프레이밍 축 선택과 같은 성격의 합의).
/// </para>
/// <para>
/// <b>실패는 값이다</b>(T-16). <see cref="TryDecode"/> 는 원격 입력이 만드는 실패
/// (손상·폭탄·알고리즘 불일치)에 예외를 던지지 않는다. 예외는 호출자 버그
/// (버퍼 부족 등)에만 쓴다.
/// </para>
/// <para>
/// <b>비압축성 데이터의 판정은 호출자 몫이다.</b> <see cref="Encode"/> 결과가 원본보다
/// 크거나 같으면 호출자가 평문 송신을 택한다(<c>Compressed</c> 플래그 없이) —
/// 그 정책은 조립(송신 헬퍼)에 있고 코덱은 기계적 변환만 한다.
/// </para>
/// <para><b>스레드 규약.</b> 구현은 무상태이거나 스레드 안전해야 한다 — 모든 커넥션이 공유한다.</para>
/// </remarks>
public interface IPayloadCodec
{
    /// <summary>원본 길이에 대한 압축 출력의 최악 크기.</summary>
    /// <param name="sourceLength">원본 바이트 수.</param>
    /// <returns><see cref="Encode"/> 에 넘길 버퍼가 가져야 할 최소 크기.</returns>
    /// <remarks>호출자가 이 값으로 버퍼를 사전 산정한다 — 압축은 최악의 경우 원본보다 커진다.</remarks>
    int MaxEncodedLength(int sourceLength);

    /// <summary>원본을 자기서술 블롭으로 압축한다.</summary>
    /// <param name="source">원본 페이로드.</param>
    /// <param name="destination">출력 버퍼. <see cref="MaxEncodedLength"/> 이상이어야 한다.</param>
    /// <returns>쓴 바이트 수.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/>이 짧을 때 — 호출자 버그다.</exception>
    int Encode(ReadOnlySpan<byte> source, Span<byte> destination);

    /// <summary>블롭을 해제해 <paramref name="destination"/>에 쓴다.</summary>
    /// <param name="source">수신한 블롭.</param>
    /// <param name="destination">해제 출력. 성공 시 정확히 <paramref name="decodedLength"/> 바이트가 쓰인다.</param>
    /// <param name="maxDecodedLength">허용하는 최대 해제 크기. 블롭의 선언 길이를
    /// <b>버퍼를 잡기 전에</b> 이 값과 대조한다(T-18).</param>
    /// <param name="decodedLength">성공하면 해제된 바이트 수.</param>
    /// <returns>
    /// 성공이면 <see langword="true"/>. 손상·상한 초과·선언 길이와 실제 불일치는
    /// 전부 <see langword="false"/> — 호출자는 커넥션을 닫는다.
    /// </returns>
    bool TryDecode(
        in ReadOnlySequence<byte> source,
        IBufferWriter<byte> destination,
        int maxDecodedLength,
        out int decodedLength);
}
