using System;
using System.Collections.Generic;
using System.Threading;

namespace ChServerM.DataTable;

/// <summary>테이블 다시 읽기의 결과.</summary>
/// <remarks>
/// <b>실패가 예외가 아닌 이유.</b> 재적재 실패는 <b>정상적으로 일어나는 일</b>이다 —
/// 사람이 표를 고치다 오타를 내는 것이 그 대부분이다. 예외로 던지면 호출 지점(관리
/// 엔드포인트·스케줄러)마다 <c>try/catch</c> 가 생기고, 그중 하나가 빠뜨리면
/// <b>운영 중 서버가 표 오타로 죽는다</b>. 그것이 정확히 이 기능이 막으려던 일이다.
/// </remarks>
public readonly struct StaticTableReloadResult : IEquatable<StaticTableReloadResult>
{
    private StaticTableReloadResult(bool succeeded, int generation, StaticTableLoadException? failure)
    {
        Succeeded = succeeded;
        Generation = generation;
        Failure = failure;
    }

    /// <summary>새 데이터로 교체됐는가.</summary>
    public bool Succeeded { get; }

    /// <summary>교체 후의 세대 번호. 실패면 <b>바뀌지 않은</b> 현재 세대다.</summary>
    public int Generation { get; }

    /// <summary>실패 원인. 성공이면 <see langword="null"/>.</summary>
    /// <remarks>오류 목록이 그대로 들어 있으므로 운영자에게 무엇을 고쳐야 하는지 보여 줄 수 있다.</remarks>
    public StaticTableLoadException? Failure { get; }

    /// <summary>성공 결과를 만든다.</summary>
    /// <param name="generation">교체 후 세대.</param>
    /// <returns>성공 결과.</returns>
    public static StaticTableReloadResult Ok(int generation) => new(true, generation, null);

    /// <summary>실패 결과를 만든다.</summary>
    /// <param name="generation">유지된 현재 세대.</param>
    /// <param name="failure">원인.</param>
    /// <returns>실패 결과.</returns>
    public static StaticTableReloadResult Failed(int generation, StaticTableLoadException failure) =>
        new(false, generation, failure);

    /// <inheritdoc/>
    public bool Equals(StaticTableReloadResult other) =>
        Succeeded == other.Succeeded && Generation == other.Generation
        && ReferenceEquals(Failure, other.Failure);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is StaticTableReloadResult other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Succeeded, Generation);

    /// <summary>두 결과가 같은지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    public static bool operator ==(StaticTableReloadResult left, StaticTableReloadResult right) =>
        left.Equals(right);

    /// <summary>두 결과가 다른지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    public static bool operator !=(StaticTableReloadResult left, StaticTableReloadResult right) =>
        !left.Equals(right);
}

/// <summary>
/// 무중단으로 교체 가능한 테이블 묶음 — <b>검증에 성공했을 때만 바뀐다</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 밸런스 표를 고치려고 서버를 재기동하는 것은 실서비스에서 큰 비용이다.
/// 레거시에는 <b>승계할 구현이 없다</b>(<c>FileWatcherSystemM</c> 은 참조 0,
/// docs/legacy/11-data-table) — 처음부터 설계한다.
/// </para>
///
/// <para>
/// <b>⭐ 문제의 전부는 "교체를 원자적으로 만드는 것" 이다.</b> 테이블은 이미 불변이므로
/// (<see cref="StaticTable"/>) 값을 제자리에서 고칠 일이 없다. 남는 것은 <b>참조 하나를
/// 바꾸는 것</b>뿐이고, 그것은 <see cref="Volatile"/> 읽기·쓰기로 끝난다. 락도, 읽기 쪽
/// 동기화도 필요 없다 — <b>불변이 동시성 문제를 미리 없앤 결과</b>다.
/// </para>
///
/// <para>
/// <b>⚠⚠ 기동과 재적재의 실패 처리는 정반대다.</b>
/// </para>
/// <list type="bullet">
///   <item><b>기동</b>: 검증 실패 = <b>기동 실패</b>. 잘못된 데이터로 서비스를 시작하면
///   안 된다(ADR-0041)</item>
///   <item><b>재적재</b>: 검증 실패 = <b>옛 데이터를 그대로 유지</b>하고 실패를 보고한다.
///   <b>돌고 있는 서버를 표 오타로 죽이면 안 된다</b> — 그것이 정확히 이 기능이 막으려던 일이다</item>
/// </list>
/// <para>
/// 그래서 순서가 <b>"새로 만들고 → 검증하고 → 성공했을 때만 교체"</b> 다. 교체 시점에는
/// 이미 검증이 끝나 있으므로 <b>절반만 바뀐 상태가 존재할 수 없다</b>.
/// </para>
///
/// <para>
/// <b>⚠ 한 작업은 <see cref="Current"/> 를 한 번만 읽는다.</b> 표를 조회할 때마다 다시
/// 읽으면 <b>같은 요청 안에서 옛 표 A 와 새 표 B 를 섞어 보게</b> 된다 — 참조 무결성이
/// 보장된 묶음을 만들어 놓고 그것을 가로지르는 셈이다. 한 번 받은 묶음을 그 작업이 끝날
/// 때까지 쓴다.
/// </para>
///
/// <para>
/// <b>파일 감시기를 넣지 않는다.</b> 언제 다시 읽을지는 <b>정책</b>이다 — 관리 엔드포인트,
/// 배포 훅, 스케줄러 중 무엇이 맞는지는 배포마다 다르다. 그리고 파일 감시는 부분 기록(에디터가
/// 저장 중인 파일)과 이벤트 폭풍을 스스로 다뤄야 하는데, 그 처리는 <b>감시기의 문제이지
/// 테이블의 문제가 아니다</b>. 여기서는 <b>메커니즘</b>만 준다.
/// </para>
///
/// <para>
/// <b>스레드 규약 — 읽기는 완전히 안전하다.</b> <see cref="Current"/> 는 여러 스레드에서
/// 동시에 읽어도 되고, 읽는 도중 교체가 일어나도 <b>받은 묶음은 계속 유효하다</b>(불변이므로).
/// <see cref="TryReload(Func{StaticTableSet})"/> 는 <b>한 번에 하나만</b> 실행된다 — 동시 재적재는 순서가 뒤집혀
/// 옛 데이터가 이길 수 있으므로 잠금으로 직렬화한다.
/// </para>
/// </remarks>
public sealed class ReloadableStaticTableSet
{
    private readonly Lock _reloadGate = new();
    private StaticTableSet _current;
    private int _generation;

    /// <summary>초기 묶음으로 만든다.</summary>
    /// <param name="initial">기동 시 검증을 통과한 묶음.</param>
    /// <exception cref="ArgumentNullException"><paramref name="initial"/> 가 <see langword="null"/> 이다.</exception>
    /// <remarks>
    /// <b>초기 묶음은 이미 검증된 것을 받는다.</b> 기동 시 검증 실패는 기동 실패여야 하므로
    /// (ADR-0041) 그 실패는 이 타입이 아니라 <see cref="StaticTableSetBuilder.Build"/> 에서 난다.
    /// </remarks>
    public ReloadableStaticTableSet(StaticTableSet initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
        _generation = 1;
    }

    /// <summary>
    /// 지금 유효한 묶음. <b>한 작업에서 한 번만 읽는다</b>(타입 문서 참조).
    /// </summary>
    public StaticTableSet Current => Volatile.Read(ref _current);

    /// <summary>
    /// 현재 세대 번호. 교체될 때마다 1 씩 는다.
    /// </summary>
    /// <remarks>
    /// 소비자가 <b>교체가 일어났는지</b> 값싸게 확인할 수 있게 한다 — 파생 캐시를 들고 있는
    /// 쪽은 세대가 바뀌었을 때만 다시 만들면 된다. 클라이언트와 표 버전을 대조하는 항목
    /// (Phase 14)도 이 위에 얹힌다.
    /// </remarks>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>새 데이터를 만들어 검증하고, 성공했을 때만 교체한다.</summary>
    /// <param name="build">
    /// 새 묶음을 만드는 함수. <b>이 안에서 파일을 읽는다</b> — 읽기·파싱·검증이 전부
    /// 교체 <b>전에</b> 끝나야 하기 때문이다.
    /// </param>
    /// <returns>성공 여부와 세대. 실패면 원인이 함께 온다.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="build"/> 가 <see langword="null"/> 이다.</exception>
    /// <remarks>
    /// <para>
    /// <b>검증 실패는 예외가 아니라 결과다</b>(<see cref="StaticTableReloadResult"/> 문서).
    /// 그리고 실패해도 <b>현재 묶음은 손대지 않는다</b> — 서버는 옛 데이터로 계속 서비스한다.
    /// </para>
    /// <para>
    /// <b>⚠ <paramref name="build"/> 가 던지는 다른 예외는 그대로 전파한다.</b> 파일이 없거나
    /// 권한이 없는 것은 <b>데이터 오류가 아니라 환경 오류</b>이고, 그것까지 조용히 삼키면
    /// "재적재가 계속 실패하는데 아무도 모르는" 상태가 된다.
    /// </para>
    /// </remarks>
    public StaticTableReloadResult TryReload(Func<StaticTableSet> build)
    {
        ArgumentNullException.ThrowIfNull(build);

        // 동시 재적재를 직렬화한다 — 두 재적재가 겹치면 순서가 뒤집혀 옛 데이터가 이길 수 있다.
        lock (_reloadGate)
        {
            StaticTableSet candidate;

            try
            {
                candidate = build();
            }
            catch (StaticTableLoadException failure)
            {
                // ⚠ 여기서 교체하지 않는다. 돌고 있는 서버를 표 오타로 죽이지 않는 것이 요점이다.
                return StaticTableReloadResult.Failed(Volatile.Read(ref _generation), failure);
            }

            ArgumentNullException.ThrowIfNull(candidate);

            // 검증이 끝난 뒤에 참조 하나만 바꾼다 — 절반만 바뀐 상태가 존재할 수 없다.
            Volatile.Write(ref _current, candidate);
            int generation = Interlocked.Increment(ref _generation);

            return StaticTableReloadResult.Ok(generation);
        }
    }

    /// <summary>CSV 원본 목록으로 다시 읽는다.</summary>
    /// <param name="sources">스키마와 CSV 내용의 쌍.</param>
    /// <returns>성공 여부와 세대.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/> 가 <see langword="null"/> 이다.</exception>
    /// <remarks>흔한 경우를 위한 편의 오버로드다. 파일에서 읽는 것은 호출자의 몫이다.</remarks>
    public StaticTableReloadResult TryReload(IReadOnlyList<(StaticTableSchema Schema, string Content)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        return TryReload(() =>
        {
            StaticTableSetBuilder builder = new();
            foreach ((StaticTableSchema schema, string content) in sources)
            {
                builder.Add(schema, content);
            }

            return builder.Build();
        });
    }
}
