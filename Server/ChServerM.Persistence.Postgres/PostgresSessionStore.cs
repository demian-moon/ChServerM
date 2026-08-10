using System;
using System.Buffers;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Identity;
using ChServerM.Sessions;
using Npgsql;

namespace ChServerM.Persistence.Postgres;

/// <summary>
/// <see cref="ISessionStore"/> 의 PostgreSQL 어댑터 — 축의 <b>세 번째 구현</b>이다.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 인메모리(참조)와 Redis(원격 KV)에 이어 <b>관계형</b> 저장소가 같은
/// 계약을 만족하는지 확인한다. 성격이 가장 다른 구현이므로 축이 진짜로 교체 가능한지에
/// 대한 가장 강한 증거가 된다 — 적합성 스위트를 그대로 통과해야 한다(ADR-0034 의 규율).
/// 세션을 다른 업무 데이터와 <b>같은 트랜잭션 경계</b> 안에서 다루고 싶은 배포에서 값이 있다.
/// </para>
///
/// <para>
/// <b>⚠ CAS 는 조건부 <c>UPDATE</c> 다 — 스크립트도 잠금도 필요 없다.</b>
/// <c>UPDATE ... WHERE id = @id AND version = @expected</c> 는 그 자체로 원자적이고,
/// 영향 행 수가 0 이면 곧 충돌이다. Redis 가 Lua 를 써야 했던 것(읽고-비교-쓰기를 한 왕복에
/// 묶기 위해)을 관계형은 <b>공짜로</b> 제공한다.
/// </para>
///
/// <para>
/// <b>버전은 전역 <c>SEQUENCE</c> 가 발급한다.</b> 행별 카운터가 아닌 이유는 <b>ABA 방지</b>다 —
/// 세션이 삭제·만료된 뒤 다시 만들어져도 이전 버전이 재사용되지 않아야 오래된 쓰기가
/// 성공하지 않는다(<see cref="SessionVersion"/> 계약 2번). 시퀀스는 되감지 않는 한 그것을 보장한다.
/// </para>
///
/// <para>
/// <b>⚠ 만료를 직접 지워야 한다.</b> PostgreSQL 에는 네이티브 TTL 이 없다. 그래서 이 구현은
/// 인메모리와 같은 모양이 된다 — <b>지연 판정</b>(모든 조회에
/// <c>expires_at IS NULL OR expires_at &gt; now()</c> 를 붙인다) + <b>주기적 청소</b>.
/// 지연 판정만으로는 다시 조회되지 않는 세션이 테이블에 영원히 남는다.
/// <b>같은 계약을 각 저장소의 수단으로 만족시키는 것</b>이 축의 요점이고, 여기서는 그 수단이
/// 주기적 <c>DELETE</c> 다.
/// </para>
///
/// <para>
/// <b>⚠ 시간 기준은 데이터베이스 서버의 <c>now()</c> 다.</b> 만료 판정을 애플리케이션 시계로
/// 하면 노드마다 다른 답을 낸다. 그래서 만료 시각 계산도 서버에서 한다
/// (<c>now() + @ttl</c>) — <see cref="TimeProvider"/> 를 받지 않는 이유이며, 인메모리 구현과
/// 다른 점이다.
/// </para>
///
/// <para>
/// <b>스키마는 자동으로 만들지 않는다.</b> <see cref="EnsureSchemaAsync"/> 를 명시적으로
/// 부른다. 프레임워크가 조용히 DDL 을 실행하면 운영자가 스키마 변경 시점을 통제할 수 없고,
/// 마이그레이션 도구와 충돌한다.
/// </para>
///
/// <para>
/// <b>스레드 규약 — 스레드 안전하다.</b> <see cref="NpgsqlDataSource"/> 가 스레드 안전하며
/// 이 타입은 불변 상태만 갖는다.
/// </para>
///
/// <para>
/// <b>수명·소유권 규약.</b> 데이터 원본의 소유권은 <b>호출자에게 있다</b> — 이 타입은
/// 닫지 않는다(<see cref="IDisposable"/> 이 아니다). 커넥션 풀은 애플리케이션당 하나를
/// 공유하는 것이 Npgsql 의 권장 사용법이므로 어댑터가 남의 자원을 닫으면 안 된다.
/// 청소 타이머만 이 타입의 것이며 <see cref="DisposeAsync"/> 가 정리한다.
/// </para>
/// </remarks>
public sealed class PostgresSessionStore : ISessionStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresSessionStoreOptions _options;
    private readonly ITimer? _sweepTimer;

    private readonly string _readSql;
    private readonly string _createSql;
    private readonly string _updateSql;
    private readonly string _removeSql;
    private readonly string _renewSql;
    private readonly string _sweepSql;

    private volatile bool _disposed;

    /// <summary>PostgreSQL 세션 저장소를 만든다.</summary>
    /// <param name="dataSource">Npgsql 데이터 원본. <b>소유권은 호출자에게 있다.</b></param>
    /// <param name="options">설정. <see langword="null"/> 이면 기본값.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> 가 <see langword="null"/> 이다.</exception>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않다.</exception>
    public PostgresSessionStore(NpgsqlDataSource dataSource, PostgresSessionStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _options = options ?? new PostgresSessionStoreOptions();
        _options.Validate();
        _dataSource = dataSource;

        string table = _options.TableName;
        string sequence = _options.VersionSequenceName;

        // 살아 있는 행만 본다 — 만료 판정을 모든 조회에 붙이는 것이 "만료된 항목은 없는 것과
        // 같다" 는 계약을 지키는 방법이다(청소는 메모리 회수용이지 판정용이 아니다).
        const string Alive = "(expires_at IS NULL OR expires_at > now())";

        _readSql = $"SELECT version, state FROM {table} WHERE id = @id AND {Alive};";

        // 생성: 없거나 **만료된** 행일 때만 성공한다. 살아 있는 행이 있으면 0 행 = 충돌.
        _createSql = $"""
            INSERT INTO {table} (id, version, state, expires_at)
            VALUES (@id, nextval('{sequence}'), @state,
                    CASE WHEN @ttl_ms > 0 THEN now() + (@ttl_ms || ' milliseconds')::interval ELSE NULL END)
            ON CONFLICT (id) DO UPDATE
                SET version = EXCLUDED.version, state = EXCLUDED.state, expires_at = EXCLUDED.expires_at
                WHERE {table}.expires_at IS NOT NULL AND {table}.expires_at <= now()
            RETURNING version;
            """;

        // 갱신: 기대 버전이 맞고 살아 있을 때만. 영향 행 0 = 충돌.
        _updateSql = $"""
            UPDATE {table}
            SET version = nextval('{sequence}'), state = @state,
                expires_at = CASE WHEN @ttl_ms > 0 THEN now() + (@ttl_ms || ' milliseconds')::interval ELSE NULL END
            WHERE id = @id AND version = @expected AND {Alive}
            RETURNING version;
            """;

        _removeSql = $"DELETE FROM {table} WHERE id = @id AND version = @expected AND {Alive};";

        // ⚠ 버전을 올리지 않는다 — 상태가 바뀌지 않았으므로 다른 주체의 CAS 를 깨면 안 된다(계약).
        _renewSql = $"""
            UPDATE {table}
            SET expires_at = now() + (@ttl_ms || ' milliseconds')::interval
            WHERE id = @id AND version = @expected AND {Alive};
            """;

        // 상한을 둔다 — 만료 행이 수백만이면 무제한 DELETE 가 긴 잠금으로 서비스를 막는다.
        _sweepSql = $"""
            DELETE FROM {table}
            WHERE id IN (
                SELECT id FROM {table}
                WHERE expires_at IS NOT NULL AND expires_at <= now()
                LIMIT @batch);
            """;

        if (_options.SweepInterval is { } interval)
        {
            // 저장소당 타이머 하나. 세션마다 만들지 않는다(CLAUDE.md 9.5).
            _sweepTimer = TimeProvider.System.CreateTimer(
                static state => _ = ((PostgresSessionStore)state!).SweepSafeAsync(),
                this,
                interval,
                interval);
        }
    }

    /// <summary>테이블과 시퀀스를 만든다(없을 때만).</summary>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>비동기 작업.</returns>
    /// <remarks>
    /// <b>자동으로 부르지 않는다.</b> 프레임워크가 조용히 DDL 을 실행하면 운영자가 스키마
    /// 변경 시점을 통제할 수 없고 마이그레이션 도구와 충돌한다. 개발·테스트 편의용이며,
    /// 운영에서는 이 정의를 마이그레이션에 옮겨 적는 것을 권한다.
    /// </remarks>
    public async ValueTask EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        string sql = $"""
            CREATE TABLE IF NOT EXISTS {_options.TableName} (
                id         bigint PRIMARY KEY,
                version    bigint NOT NULL,
                state      bytea  NOT NULL,
                expires_at timestamptz NULL);
            CREATE SEQUENCE IF NOT EXISTS {_options.VersionSequenceName} AS bigint START 1;
            CREATE INDEX IF NOT EXISTS {_options.TableName}_expires_at_idx
                ON {_options.TableName} (expires_at) WHERE expires_at IS NOT NULL;
            """;

        NpgsqlCommand command = _dataSource.CreateCommand(sql);
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<SessionReadResult> TryReadAsync(
        SessionId id,
        IBufferWriter<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NpgsqlCommand command = _dataSource.CreateCommand(_readSql);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("id", id.Value.Value);

            NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            await using (reader.ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    // ⚠ 찾지 못하면 대상을 건드리지 않는다(계약).
                    return SessionReadResult.NotFound;
                }

                long version = reader.GetInt64(0);
                byte[] state = (byte[])reader.GetValue(1);
                destination.Write(state);

                return SessionReadResult.Hit(new SessionVersion(unchecked((ulong)version)), state.Length);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask<SessionWriteResult> TryWriteAsync(
        SessionId id,
        ReadOnlyMemory<byte> state,
        SessionVersion expectedVersion,
        TimeSpan? timeToLive = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfInvalidTtl(timeToLive);

        bool creating = expectedVersion.IsNone;

        NpgsqlCommand command = _dataSource.CreateCommand(creating ? _createSql : _updateSql);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("id", id.Value.Value);
            command.Parameters.AddWithValue("state", state.ToArray());
            command.Parameters.AddWithValue("ttl_ms", (long)(timeToLive?.TotalMilliseconds ?? 0));

            if (!creating)
            {
                command.Parameters.AddWithValue("expected", unchecked((long)expectedVersion.Value));
            }

            object? version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            // RETURNING 이 비면 조건이 맞지 않은 것 — 충돌이다(예외가 아니라 정상 결과).
            return version is long raw
                ? SessionWriteResult.Ok(new SessionVersion(unchecked((ulong)raw)))
                : SessionWriteResult.Conflict;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<bool> TryRemoveAsync(
        SessionId id,
        SessionVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (expectedVersion.IsNone)
        {
            // 없는 항목을 지우라는 요청 — 계약상 false 다(지울 것이 없다).
            return false;
        }

        NpgsqlCommand command = _dataSource.CreateCommand(_removeSql);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("id", id.Value.Value);
            command.Parameters.AddWithValue("expected", unchecked((long)expectedVersion.Value));

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<bool> TryRenewAsync(
        SessionId id,
        SessionVersion expectedVersion,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfInvalidTtl(timeToLive);

        if (expectedVersion.IsNone)
        {
            return false;
        }

        NpgsqlCommand command = _dataSource.CreateCommand(_renewSql);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("id", id.Value.Value);
            command.Parameters.AddWithValue("expected", unchecked((long)expectedVersion.Value));
            command.Parameters.AddWithValue("ttl_ms", (long)timeToLive.TotalMilliseconds);

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
    }

    /// <summary>만료 행을 한 배치 지운다.</summary>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>지운 행 수.</returns>
    /// <remarks>
    /// 타이머가 주기적으로 부르지만, 외부 스케줄러가 청소를 맡는 배포에서는 직접 불러도 된다
    /// (그 경우 <see cref="PostgresSessionStoreOptions.SweepInterval"/> 을 <see langword="null"/> 로 둔다).
    /// </remarks>
    public async ValueTask<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        NpgsqlCommand command = _dataSource.CreateCommand(_sweepSql);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("batch", _options.SweepBatchSize);

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_sweepTimer is not null)
        {
            await _sweepTimer.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>타이머 콜백 — 예외가 프로세스를 죽이지 않게 삼킨다.</summary>
    /// <remarks>
    /// <b>타이머 콜백에서 새어 나간 예외는 잡을 사람이 없다.</b> 청소 실패는 다음 주기에
    /// 다시 시도하면 되는 일이므로, 여기서 죽는 것이 훨씬 나쁘다(CLAUDE.md 9.2 의 취지).
    /// </remarks>
    private async Task SweepSafeAsync()
    {
        try
        {
            if (!_disposed)
            {
                await SweepAsync().ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // 청소는 최선 노력이다. 다음 주기에 다시 시도한다.
        catch (Exception)
        {
            // 연결 끊김·잠금 대기 초과 등. 다음 주기에 다시 한다.
        }
#pragma warning restore CA1031
    }

    private static void ThrowIfInvalidTtl(TimeSpan? timeToLive)
    {
        if (timeToLive is { } ttl && ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeToLive), ttl, "만료 시간은 0 보다 커야 한다(만료 없음은 null).");
        }
    }
}
