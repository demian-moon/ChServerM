using System;

namespace ChServerM.Persistence.Postgres;

/// <summary>
/// <see cref="PostgresSessionStore"/> 설정.
/// </summary>
public sealed class PostgresSessionStoreOptions
{
    /// <summary>세션 테이블 이름의 기본값.</summary>
    public const string DefaultTableName = "chsm_session";

    /// <summary>버전 시퀀스 이름의 기본값.</summary>
    public const string DefaultVersionSequenceName = "chsm_session_version";

    /// <summary>
    /// 세션 테이블 이름. 스키마를 함께 쓰려면 <c>myschema.chsm_session</c> 처럼 적는다.
    /// </summary>
    /// <remarks>
    /// <b>⚠ 이 값은 SQL 에 그대로 삽입된다.</b> 식별자는 매개변수로 바인딩할 수 없기 때문이다.
    /// 그래서 <see cref="Validate"/> 가 허용 문자를 <b>화이트리스트</b>로 검사한다 —
    /// 사용자 입력에서 오는 값을 여기 넣지 않는다.
    /// </remarks>
    public string TableName { get; set; } = DefaultTableName;

    /// <summary>
    /// 버전 발급 시퀀스 이름.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>⚠ 시퀀스를 지우거나 되돌리지 않는다.</b> 되감기면 <b>이전 버전이 재사용되어 ABA 가
    /// 난다</b> — 오래된 버전을 든 쓰기가 성공해 남의 상태를 덮는다(<c>SessionVersion</c> 계약 2번).
    /// </para>
    /// <para>
    /// 행별 카운터가 아니라 전역 시퀀스인 이유도 같다. 행별이면 세션이 삭제될 때 함께
    /// 사라져 재생성 시 1 부터 다시 시작한다.
    /// </para>
    /// </remarks>
    public string VersionSequenceName { get; set; } = DefaultVersionSequenceName;

    /// <summary>
    /// 만료 항목 청소 주기. <see langword="null"/> 이면 청소하지 않는다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>⚠ PostgreSQL 에는 네이티브 만료가 없다.</b> Redis 는 서버가 회수해 주지만 여기서는
    /// 어댑터가 직접 지워야 한다 — <b>지연 판정만으로는 다시 조회되지 않는 세션</b>(끊긴
    /// 클라이언트의 상태)이 테이블에 영원히 남는다. 인메모리 구현과 같은 이유로 청소가 필요하다.
    /// </para>
    /// <para>
    /// <b>같은 계약을 각 저장소의 수단으로 만족시키는 것</b>이 축의 요점이며, 여기서는
    /// 그 수단이 주기적 <c>DELETE</c> 다.
    /// </para>
    /// <para>
    /// <see langword="null"/> 로 끄는 것은 외부 배치(cron·pg_cron)가 청소를 맡는 배포를 위한 선택지다.
    /// </para>
    /// </remarks>
    public TimeSpan? SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>한 번의 청소에서 지울 최대 행 수.</summary>
    /// <remarks>
    /// <b>상한이 없으면 청소가 장애를 만든다.</b> 만료 행이 수백만 개 쌓인 상태에서 무제한
    /// <c>DELETE</c> 는 긴 트랜잭션과 잠금으로 서비스 쿼리를 막는다. 나눠서 지운다.
    /// </remarks>
    public int SweepBatchSize { get; set; } = 1000;

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않다.</exception>
    public void Validate()
    {
        EnsureSafeIdentifier(TableName, nameof(TableName));
        EnsureSafeIdentifier(VersionSequenceName, nameof(VersionSequenceName));

        if (SweepInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{nameof(SweepInterval)} 은 0 보다 커야 한다(끄려면 null). 현재: {interval}");
        }

        if (SweepBatchSize < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(SweepBatchSize)} 은 1 이상이어야 한다. 현재: {SweepBatchSize}");
        }
    }

    /// <summary>SQL 에 직접 삽입되는 식별자를 화이트리스트로 검사한다.</summary>
    /// <remarks>
    /// 소문자·숫자·밑줄·점만 허용한다. 인용 부호나 세미콜론이 들어올 여지를 아예 없앤다 —
    /// <b>이스케이프하는 대신 거부하는 편이 안전하다.</b>
    /// </remarks>
    private static void EnsureSafeIdentifier(string value, string name)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                $"{name} 은 비어 있을 수 없다. 이 값은 SQL 식별자로 직접 삽입되므로 "
                + "소문자·숫자·밑줄·점만 허용된다 — 기본값을 바꿀 이유가 없다면 그대로 둔다.");
        }

        foreach (char c in value)
        {
            bool allowed = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.';
            if (!allowed)
            {
                throw new InvalidOperationException(
                    $"{name} 에 허용되지 않는 문자가 있다: '{c}'. 소문자·숫자·밑줄·점만 쓴다(SQL 에 직접 삽입되므로).");
            }
        }
    }
}
