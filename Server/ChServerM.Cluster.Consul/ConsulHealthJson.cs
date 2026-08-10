using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ChServerM.Cluster.Consul;

/// <summary>
/// <c>/v1/health/service/{name}</c> 응답에서 <b>우리가 쓰는 부분만</b> 담는 DTO.
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 전체를 모델링하지 않는가.</b> Consul 응답에는 체크·가중치·태그·데이터센터 등이
/// 딸려 오지만 이 어댑터가 쓰는 것은 <b>주소·포트·메타</b> 셋뿐이다. 안 쓰는 필드를
/// 모델에 넣으면 Consul 이 스키마를 넓힐 때마다 이쪽이 따라 움직여야 한다 —
/// <c>System.Text.Json</c> 은 모르는 필드를 조용히 무시하므로 <b>좁게 두는 편이 안정적</b>이다.
/// </para>
/// <para>
/// <b>⚠ 대소문자는 Consul 이 정한다.</b> 응답 키는 <c>Service</c>·<c>Address</c> 처럼
/// 파스칼 케이스이므로 <see cref="JsonPropertyNameAttribute"/> 로 못 박는다 —
/// 명명 정책에 기대면 기본값이 바뀌는 날 조용히 빈 목록이 된다.
/// </para>
/// </remarks>
internal sealed class ConsulHealthEntry
{
    /// <summary>서비스 등록 정보.</summary>
    [JsonPropertyName("Service")]
    public ConsulService? Service { get; set; }
}

/// <summary>서비스 등록의 주소·포트·메타.</summary>
internal sealed class ConsulService
{
    /// <summary>Consul 안에서 이 인스턴스를 가리키는 ID. 진단 메시지에만 쓴다.</summary>
    [JsonPropertyName("ID")]
    public string? Id { get; set; }

    /// <summary>서비스 주소. 비어 있으면 노드 주소를 쓰는 것이 Consul 규약이다.</summary>
    [JsonPropertyName("Address")]
    public string? Address { get; set; }

    /// <summary>서비스 포트.</summary>
    [JsonPropertyName("Port")]
    public int Port { get; set; }

    /// <summary>사용자 메타. 노드 번호와 노드 간 포트가 여기 담긴다.</summary>
    [JsonPropertyName("Meta")]
    public Dictionary<string, string>? Meta { get; set; }
}

/// <summary>
/// 소스 생성 직렬화 컨텍스트 — <b>리플렉션을 쓰지 않기 위해서다</b>.
/// </summary>
/// <remarks>
/// 런타임 리플렉션 금지는 이 프로젝트의 하드 룰이고(CLAUDE.md 2절), Native AOT 에서
/// 리플렉션 기반 <c>JsonSerializer</c> 는 <b>트리밍에 잘려 런타임에 실패</b>한다.
/// 컴파일 타임에 생성해 두면 그 실패가 존재하지 않는다.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(ConsulHealthEntry[]))]
internal sealed partial class ConsulJsonContext : JsonSerializerContext
{
}
