using System;
using System.Buffers;
using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using ChServerM.Buffers;
using ChServerM.Identity;
using ChServerM.Persistence.InMemory;
using ChServerM.Sessions;

namespace ChServerM.Bench.Sessions;

/// <summary>
/// <b>세션 축(ADR-0033)의 가격표</b> — 바이트 계약과 CAS 가 실제로 얼마를 먹는가.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> ADR-0033 은 값을 <b>바이트</b>로 정하면서 "인메모리에서도 복사 비용을
/// 낸다 — 알고 고른 값" 이라고 적었다. <b>알고 골랐다면 얼마인지도 알아야 한다.</b> 이
/// 벤치마크가 그 청구서다. 축 추가 순서(CLAUDE.md 3절: Core 인터페이스 → 참조 구현 →
/// <b>벤치마크</b> → 두 번째 구현)의 세 번째 단계이기도 하다.
/// </para>
/// <para>
/// <b>기준선은 맨 <see cref="ConcurrentDictionary{TKey,TValue}"/> 다.</b> 계약(값 복사 +
/// 버전 검사 + 만료 판정)을 전부 걷어낸 형태이므로, 차이가 곧 <b>계약의 가격</b>이다.
/// raw Kestrel 바닥선(2026-08-09)과 같은 방법론 — 프레임워크는 자기 가격표를 알아야 한다.
/// </para>
/// <para>
/// <b>⚠ 기준선은 의도적으로 우리에게 불리하다.</b> 맨 사전은 참조를 그대로 돌려주므로
/// 복사가 0 이고, 버전도 만료도 보지 않는다. 즉 <b>기능이 다르다</b> — 여기서 나오는 차이는
/// "느리다" 가 아니라 <b>"값 의미·CAS·만료를 얻는 값"</b>으로 읽어야 한다.
/// </para>
/// <para>
/// <b>대상 버퍼는 재사용한다.</b> 읽기마다 <c>ArrayBufferWriter</c> 를 새로 만들면 재는 것이
/// 저장소가 아니라 할당이다. 실제 호출부도 커넥션·요청 스코프에서 풀 라이터를 재사용한다.
/// </para>
/// </remarks>
[Config(typeof(BenchConfig))]
// CA2012 억제 — 이 벤치마크가 재는 InMemorySessionStore 는 **항상 동기 완료**한다
// (I/O 가 없다). BenchmarkDotNet 의 async 지원을 쓰면 측정에 상태 머신 비용이 섞여
// 재려는 대상(계약의 가격)이 아니라 async 기계 장치를 재게 된다. 원격 저장소(Redis)
// 벤치마크는 진짜로 비동기이므로 그때는 async 벤치마크로 써야 한다.
#pragma warning disable CA2012
public class SessionStoreBenchmarks
{
    /// <summary>세션 상태 크기. 작은 상태(플래그 몇 개)와 큰 상태(인벤토리 등)를 함께 본다.</summary>
    [Params(64, 1024)]
    public int StateLength { get; set; }

    private InMemorySessionStore _store = null!;
    private ConcurrentDictionary<SessionId, byte[]> _baseline = null!;
    private PooledBufferWriter _destination = null!;

    private byte[] _state = null!;
    private SessionId _existing;
    private SessionId _missing;

    /// <summary>CAS 쓰기는 성공할 때마다 버전이 바뀌므로 다음 호출을 위해 들고 있어야 한다.</summary>
    private SessionVersion _rollingVersion;

    [GlobalSetup]
    public void Setup()
    {
        _state = new byte[StateLength];
#pragma warning disable CA5394 // 측정용 페이로드 — 보안 난수가 필요 없다.
        Random.Shared.NextBytes(_state);
#pragma warning restore CA5394

        // 만료 청소를 끈다 — 재는 것은 조회·갱신 경로이지 타이머가 아니다.
        _store = new InMemorySessionStore(new InMemorySessionStoreOptions { SweepInterval = null });
        _baseline = new ConcurrentDictionary<SessionId, byte[]>();
        _destination = new PooledBufferWriter(StateLength);

        _existing = new SessionId(new ObjectId(1));
        _missing = new SessionId(new ObjectId(999_999));

        SessionWriteResult created = _store
            .TryWriteAsync(_existing, _state, SessionVersion.None, TimeSpan.FromHours(1))
            .GetAwaiter().GetResult();
        _rollingVersion = created.Version;

        _baseline[_existing] = _state;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        _destination.Dispose();
    }

    // ── 기준선: 계약이 없는 맨 사전 ─────────────────────────────────────────

    [Benchmark(Baseline = true, Description = "기준선: ConcurrentDictionary 조회 (복사·버전·만료 없음)")]
    public int BaselineRead()
    {
        _baseline.TryGetValue(_existing, out byte[]? value);
        return value!.Length;
    }

    [Benchmark(Description = "기준선: ConcurrentDictionary 쓰기")]
    public void BaselineWrite() => _baseline[_existing] = _state;

    // ── 계약이 붙은 경로 ────────────────────────────────────────────────────

    [Benchmark(Description = "TryRead 적중 (대상에 복사)")]
    public int ReadHit()
    {
        _destination.Clear();
        SessionReadResult result = _store.TryReadAsync(_existing, _destination).GetAwaiter().GetResult();
        return result.Length;
    }

    [Benchmark(Description = "TryRead 미적중 (대상 미변경)")]
    public bool ReadMiss()
    {
        _destination.Clear();
        return _store.TryReadAsync(_missing, _destination).GetAwaiter().GetResult().Found;
    }

    [Benchmark(Description = "TryWrite CAS 성공 (상태 복사 + 버전 교체)")]
    public bool WriteCas()
    {
        SessionWriteResult result = _store
            .TryWriteAsync(_existing, _state, _rollingVersion, TimeSpan.FromHours(1))
            .GetAwaiter().GetResult();

        // 다음 반복이 최신 버전을 써야 한다 — 실패하면 그 뒤가 전부 충돌 경로가 되어
        // 재는 대상이 바뀐다(성공 경로가 아니라 실패 경로를 재게 된다).
        _rollingVersion = result.Version;
        return result.Succeeded;
    }

    [Benchmark(Description = "TryWrite CAS 충돌 (거부 경로)")]
    public bool WriteConflict() =>
        // ⚠ 절대 맞지 않는 버전을 써야 한다. 처음에 1 을 넣었는데 인메모리 카운터가 1 부터
        // 나가므로 셋업의 실제 버전과 같아져 **성공 경로를 재고 있었다** — 거부 경로 벤치마크가
        // 조용히 성공 경로를 재는 것이 정확히 이 프로젝트가 경계하는 "성공처럼 보이는 실패" 다.
        _store.TryWriteAsync(_existing, _state, NeverMatchingVersion, TimeSpan.FromHours(1))
            .GetAwaiter().GetResult().Succeeded;

    /// <summary>어떤 실제 버전과도 같지 않은 값 — 단조 카운터가 여기에 닿을 일이 없다.</summary>
    private static readonly SessionVersion NeverMatchingVersion = new(ulong.MaxValue);

    [Benchmark(Description = "TryRenew (상태 재전송 없이 만료만 연장)")]
    public bool Renew() =>
        _store.TryRenewAsync(_existing, _rollingVersion, TimeSpan.FromHours(1)).GetAwaiter().GetResult();
}
#pragma warning restore CA2012
