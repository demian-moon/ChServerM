using System.Net;

namespace ChServerM.Features;

/// <summary>
/// 커넥션의 양쪽 주소를 노출하는 선택 기능.
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 <c>IConnection</c>에 직접 두지 않는가.</b> 모든 전송에 주소가 있는 것이 아니다.
/// 인메모리 루프백에는 진짜 주소가 없고, 유닉스 도메인 소켓의 주소는 IP가 아니며,
/// 프로세스 내 파이프에는 아예 개념이 없다. 인터페이스에 올리면 그런 전송들이
/// <see langword="null"/>을 반환하게 되고, 그 순간 계약이 거짓말이 된다.
/// </para>
/// <para>
/// 기능으로 두면 상위 계층이 <c>Get&lt;T&gt;()</c> 결과가 <see langword="null"/>인지로
/// <b>있는지 없는지를 정직하게</b> 판단한다. 이것이 전송 축이 교체 가능한 방식이다.
/// </para>
/// <para>
/// <b>스레드 규약.</b> 커넥션 수립 시점에 채워지고 그 뒤로는 읽기 전용이다.
/// </para>
/// <para>
/// <b>카디널리티 주의.</b> 원격 주소를 메트릭 태그로 쓰지 않는다. 시계열이 폭발한다.
/// 로그와 추적 span 속성에만 남긴다.
/// </para>
/// </remarks>
public interface IConnectionEndPointFeature
{
    /// <summary>이쪽 종단의 주소. 없으면 <see langword="null"/>.</summary>
    EndPoint? LocalEndPoint { get; }

    /// <summary>상대 종단의 주소. 없으면 <see langword="null"/>.</summary>
    EndPoint? RemoteEndPoint { get; }
}
