using System;

namespace ChServerM.Persistence.Redis;

/// <summary>
/// <see cref="RedisSessionStore"/> 설정.
/// </summary>
public sealed class RedisSessionStoreOptions
{
    /// <summary>키 접두사의 기본값.</summary>
    public const string DefaultKeyPrefix = "chsm:sess:";

    /// <summary>버전 카운터 키의 기본값.</summary>
    public const string DefaultVersionCounterKey = "chsm:sess:__ver";

    /// <summary>
    /// 세션 키 접두사. 같은 Redis 인스턴스를 다른 용도와 공유할 때 충돌을 막는다.
    /// </summary>
    /// <remarks>
    /// <b>접두사를 바꾸면 사실상 다른 저장소가 된다.</b> 배포 중에 바꾸면 기존 세션이
    /// 통째로 안 보이므로, 무중단 변경이 필요하면 마이그레이션을 따로 설계한다.
    /// </remarks>
    public string KeyPrefix { get; set; } = DefaultKeyPrefix;

    /// <summary>
    /// 버전 발급용 전역 카운터 키.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>⚠ 이 키는 만료시키지 않는다.</b> 만료되어 0 부터 다시 세면 **이전 버전이 재사용되어
    /// ABA 가 난다** — 오래된 버전을 든 쓰기가 성공해 남의 상태를 덮는다
    /// (<c>SessionVersion</c> 계약 2번).
    /// </para>
    /// <para>
    /// 키별 카운터가 아니라 전역인 이유도 같다. 키별이면 세션이 삭제될 때 카운터도 사라져
    /// 재생성 시 1 부터 다시 시작한다.
    /// </para>
    /// </remarks>
    public string VersionCounterKey { get; set; } = DefaultVersionCounterKey;

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않다.</exception>
    public void Validate()
    {
        if (string.IsNullOrEmpty(KeyPrefix))
        {
            throw new InvalidOperationException($"{nameof(KeyPrefix)} 는 비어 있을 수 없다.");
        }

        if (string.IsNullOrEmpty(VersionCounterKey))
        {
            throw new InvalidOperationException($"{nameof(VersionCounterKey)} 는 비어 있을 수 없다.");
        }
    }
}
