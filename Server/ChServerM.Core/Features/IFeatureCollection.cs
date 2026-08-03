namespace ChServerM.Features;

/// <summary>
/// 타입을 키로 삼는 확장 기능 모음.
/// </summary>
/// <remarks>
/// <para>
/// <b>이것이 전송 계층의 확장점이다.</b> TCP·인메모리·WebSocket·QUIC은 저마다 다른 것을
/// 알고 있다 — 원격 엔드포인트, TLS 세션 정보, keep-alive 설정, 루프백 짝 커넥션.
/// 이것들을 <c>IConnection</c>에 전부 올리면 인터페이스가 전송마다 부풀고
/// 결국 <b>교체 가능성이 깨진다</b>(CLAUDE.md 3장).
/// </para>
/// <para>
/// 그래서 공통 계약만 인터페이스에 두고, 전송별 선택 기능은 여기에 담는다.
/// 상위 계층은 <c>Get&lt;T&gt;()</c>가 <see langword="null"/>이면 그 기능이 없는 것으로 보고
/// 우아하게 물러선다.
/// </para>
/// <para>
/// <b>스레드 안전하지 않다.</b> 기능 등록은 커넥션 수립 시점에 단일 스레드로 끝내고,
/// 그 뒤에는 읽기만 한다. 이 규약을 깨야 한다면 그건 설계 신호다.
/// </para>
/// </remarks>
public interface IFeatureCollection
{
    /// <summary>
    /// 내용이 바뀔 때마다 증가하는 번호.
    /// </summary>
    /// <remarks>
    /// 조회 결과를 캐시한 쪽이 <b>무효화 시점을 알기 위해</b> 쓴다.
    /// 값이 그대로면 이전에 조회한 기능도 그대로다.
    /// </remarks>
    int Revision { get; }

    /// <summary>등록된 기능을 가져온다.</summary>
    /// <typeparam name="TFeature">기능 계약 타입.</typeparam>
    /// <returns>등록돼 있으면 인스턴스, 없으면 <see langword="null"/>.</returns>
    /// <remarks>없는 것은 <b>정상</b>이다. 예외를 던지지 않는다.</remarks>
    TFeature? Get<TFeature>() where TFeature : class;

    /// <summary>기능을 등록하거나 해제한다.</summary>
    /// <typeparam name="TFeature">기능 계약 타입.</typeparam>
    /// <param name="instance">등록할 인스턴스. <see langword="null"/>이면 등록을 지운다.</param>
    void Set<TFeature>(TFeature? instance) where TFeature : class;
}
