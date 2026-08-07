using System;
using System.Collections.Generic;
using System.Text;
using ChServerM.Diagnostics;

namespace ChServerM.Hosting;

/// <summary>
/// 등록된 <see cref="IDiagnosticsSource"/> 를 모아 하나의 스냅샷 텍스트로 만드는 서비스 (Phase 11).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 진단 소스는 여기저기(전송·실행 모델·버퍼 풀)에 흩어져 있다. 운영자는
/// 장애 중에 <b>한 번에</b> 보고 싶어 한다 — 이 서비스가 그 모으는 지점이다.
/// </para>
/// <para>
/// <b>구역별 <c>try/catch</c> — 한 소스의 예외가 전체 스냅샷을 깨지 않는다.</b> 진단은
/// 장애 중에 부르는 경로다. 그때 한 구역이 던져서 아무것도 못 보게 되면 진단이 장애를 키운다
/// (헬스 집계·소비 루프와 같은 격리 원칙, 9.2).
/// </para>
/// <para>
/// <b>출력은 평문이다.</b> 사람이 <c>curl</c> 로 읽는 것이 1차 용도이고, 기계가 읽어야 하면
/// <c>key=value</c> 는 파싱이 쉽다. JSON 직렬화(와 그 AOT 비용)를 들이지 않는다 —
/// 헬스 엔드포인트가 평문을 쓰는 것과 같은 근거(ADR-0024).
/// </para>
/// <para><b>스레드 규약.</b> 등록은 불변이고 소스는 스레드 안전 계약이므로 동시 조회가 안전하다.</para>
/// </remarks>
public sealed class DiagnosticsService
{
    private readonly IDiagnosticsSource[] _sources;

    /// <summary>진단 소스로 서비스를 만든다.</summary>
    /// <param name="sources">수집할 소스. 순서가 출력 구역 순서다.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/>가 <see langword="null"/>일 때.</exception>
    public DiagnosticsService(IEnumerable<IDiagnosticsSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = [.. sources];
    }

    /// <summary>등록된 구역 수.</summary>
    public int SourceCount => _sources.Length;

    /// <summary>모든 소스를 수집해 평문 스냅샷을 만든다.</summary>
    /// <returns>구역별 <c>[이름]</c> 헤더와 <c>key=value</c> 줄로 이뤄진 텍스트.</returns>
    /// <remarks>
    /// 소스가 던지면 그 구역에 <c>!error</c> 항목이 남고 나머지 구역은 그대로 수집된다.
    /// </remarks>
    public string Collect()
    {
        StringBuilder builder = new();

        foreach (IDiagnosticsSource source in _sources)
        {
            builder.Append('[').Append(source.Name).Append("]\n");
            TextWriter writer = new(builder);

            try
            {
                source.Collect(writer);
            }
#pragma warning disable CA1031 // 계약: 한 구역의 예외가 전체 스냅샷을 깨지 않는다(타입 문서).
            catch (Exception exception)
#pragma warning restore CA1031
            {
                builder.Append("!error=").Append(exception.Message).Append('\n');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>수집 결과를 <c>key=value</c> 줄로 적는 기본 수집기.</summary>
    private sealed class TextWriter(StringBuilder builder) : IDiagnosticsWriter
    {
        public void Write(string key, string? value) =>
            builder.Append(key).Append('=').Append(value ?? string.Empty).Append('\n');

        public void Write(string key, long value) =>
            builder.Append(key).Append('=').Append(value).Append('\n');
    }
}
