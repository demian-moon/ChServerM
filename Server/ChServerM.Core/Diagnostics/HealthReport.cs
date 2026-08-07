using System;
using System.Collections.Generic;

namespace ChServerM.Diagnostics;

/// <summary>
/// 프로브 하나의 헬스 집계 — 전체 상태와 체크별 결과 (Phase 11 관측).
/// </summary>
/// <remarks>
/// <para>
/// <b>집계는 "가장 나쁜 것이 이긴다".</b> <see cref="Status"/> 는 모든 <see cref="Entries"/> 중
/// 최소 <see cref="HealthStatus"/> 다(<see cref="HealthStatus"/> 값 순서 규약). 체크가 하나도
/// 없으면 <see cref="HealthStatus.Healthy"/> — 감시할 것이 없으면 문제도 없다.
/// </para>
/// <para>
/// <b>헬스 조회는 핫패스가 아니다.</b> 프로브는 초 단위로 호출되므로 리스트 할당은 무해하다.
/// 무할당 규약(핫패스)의 대상이 아니다.
/// </para>
/// </remarks>
public sealed class HealthReport
{
    /// <summary>집계 상태와 항목으로 보고서를 만든다.</summary>
    /// <param name="status">집계 상태(항목들의 최솟값).</param>
    /// <param name="entries">체크별 결과.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/>가 <see langword="null"/>일 때.</exception>
    public HealthReport(HealthStatus status, IReadOnlyList<HealthReportEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Status = status;
        Entries = entries;
    }

    /// <summary>집계 헬스 상태.</summary>
    public HealthStatus Status { get; }

    /// <summary>체크별 결과. 순서는 등록 순이다.</summary>
    public IReadOnlyList<HealthReportEntry> Entries { get; }
}

/// <summary>
/// 헬스 보고서 안의 체크 하나 — 이름과 그 결과 (Phase 11 관측).
/// </summary>
public readonly struct HealthReportEntry : IEquatable<HealthReportEntry>
{
    /// <summary>이름과 결과로 항목을 만든다.</summary>
    /// <param name="name">체크 이름(등록 시 지정).</param>
    /// <param name="status">이 체크의 상태.</param>
    /// <param name="description">사람이 읽을 설명(선택).</param>
    public HealthReportEntry(string name, HealthStatus status, string? description)
    {
        Name = name;
        Status = status;
        Description = description;
    }

    /// <summary>체크 이름.</summary>
    public string Name { get; }

    /// <summary>이 체크의 상태.</summary>
    public HealthStatus Status { get; }

    /// <summary>사람이 읽을 설명. 없을 수 있다.</summary>
    public string? Description { get; }

    /// <inheritdoc />
    public bool Equals(HealthReportEntry other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal)
        && Status == other.Status
        && string.Equals(Description, other.Description, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HealthReportEntry other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Name, Status, Description);

    /// <summary>두 항목이 같은지 비교한다.</summary>
    public static bool operator ==(HealthReportEntry left, HealthReportEntry right) => left.Equals(right);

    /// <summary>두 항목이 다른지 비교한다.</summary>
    public static bool operator !=(HealthReportEntry left, HealthReportEntry right) => !left.Equals(right);
}
