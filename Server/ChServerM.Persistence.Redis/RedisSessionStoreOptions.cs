using System;

namespace ChServerM.Persistence.Redis;

/// <summary>
/// <see cref="RedisSessionStore"/> 설정.
/// </summary>
public sealed class RedisSessionStoreOptions
{
    /// <summary>키 접두사의 기본값.</summary>
    public const string DefaultKeyPrefix = "chsm:sess:";

    /// <summary>
    /// 세션 키 접두사. 같은 Redis 인스턴스를 다른 용도와 공유할 때 충돌을 막는다.
    /// </summary>
    /// <remarks>
    /// <b>접두사를 바꾸면 사실상 다른 저장소가 된다.</b> 배포 중에 바꾸면 기존 세션이
    /// 통째로 안 보이므로, 무중단 변경이 필요하면 마이그레이션을 따로 설계한다.
    /// </remarks>
    public string KeyPrefix { get; set; } = DefaultKeyPrefix;

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않다.</exception>
    /// <remarks>
    /// 버전 카운터 키 설정이 있었으나 <b>ADR-0058 로 제거됐다</b> — 버전은 더 이상 전역
    /// 카운터가 아니라 쓰기마다 클라이언트가 발급하는 64비트 난수다. 전역 키가 사라지면서
    /// Redis Cluster 의 <c>CROSSSLOT</c> 제약과 전역 <c>INCR</c> 경합이 함께 사라졌다.
    /// </remarks>
    public void Validate()
    {
        if (string.IsNullOrEmpty(KeyPrefix))
        {
            throw new InvalidOperationException(
                $"{nameof(KeyPrefix)} 는 비어 있을 수 없다. 같은 Redis 를 쓰는 다른 서비스와 키 공간을 "
                + "가르는 유일한 장치다 — 빈 접두사는 남의 키를 밟는 사고로 이어진다(기본값 유지 권장).");
        }
    }
}
