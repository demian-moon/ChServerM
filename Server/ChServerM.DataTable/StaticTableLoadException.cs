using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace ChServerM.DataTable;

/// <summary>로딩 중 발견한 오류 하나.</summary>
/// <param name="Line">1부터 세는 파일 줄 번호. 헤더 문제면 1.</param>
/// <param name="ColumnName">관련 열 이름. 줄 전체의 문제면 <see langword="null"/>.</param>
/// <param name="Message">사람이 읽는 설명.</param>
/// <remarks>
/// <b>줄 번호가 있는 것이 요점이다.</b> "값이 잘못됐다" 만으로는 수천 줄짜리 테이블에서
/// 어디를 고쳐야 하는지 알 수 없다.
/// </remarks>
public sealed record StaticTableError(int Line, string? ColumnName, string Message)
{
    /// <summary>오류를 한 줄로 표현한다.</summary>
    public override string ToString() =>
        ColumnName is null
            ? string.Create(CultureInfo.InvariantCulture, $"{Line}행: {Message}")
            : string.Create(CultureInfo.InvariantCulture, $"{Line}행 '{ColumnName}': {Message}");
}

/// <summary>
/// 데이터 테이블 로딩이 실패했다 — <b>발견한 오류를 전부</b> 담는다.
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠ 첫 오류에서 멈추지 않는 것이 설계다.</b> 테이블은 사람이 손으로 만들고 고치는
/// 데이터다. 오류를 하나씩 알려 주면 "고치고 → 다시 띄우고 → 다음 오류" 를 반복하게 되고,
/// 오류가 20개면 그 왕복을 20번 한다. <b>한 번에 다 보여 주면 한 번에 고친다.</b>
/// </para>
/// <para>
/// <b>그리고 실패는 시작 실패여야 한다.</b> 레거시는 검증이 없어 잘못된 값이 <b>첫 조회
/// 시점에 예외가 되거나 조용히 기본값</b>이 됐다(docs/legacy/11-data-table 문제점 2).
/// 그것은 장애가 배포 몇 시간 뒤 특정 요청에서 처음 나타난다는 뜻이다 —
/// <b>기동 때 죽는 편이 훨씬 낫다</b>(Phase 2 옵션 검증과 같은 원칙).
/// </para>
/// </remarks>
public sealed class StaticTableLoadException : Exception
{
    /// <summary>기본 메시지로 예외를 만든다.</summary>
    public StaticTableLoadException()
        : base("데이터 테이블 로딩에 실패했다.") => Errors = new ReadOnlyCollection<StaticTableError>([]);

    /// <summary>메시지를 지정해 예외를 만든다.</summary>
    /// <param name="message">메시지.</param>
    public StaticTableLoadException(string message)
        : base(message) => Errors = new ReadOnlyCollection<StaticTableError>([]);

    /// <summary>메시지와 내부 예외를 지정해 예외를 만든다.</summary>
    /// <param name="message">메시지.</param>
    /// <param name="innerException">내부 예외.</param>
    public StaticTableLoadException(string message, Exception innerException)
        : base(message, innerException) => Errors = new ReadOnlyCollection<StaticTableError>([]);

    /// <summary>테이블 이름과 오류 목록으로 예외를 만든다.</summary>
    /// <param name="tableName">테이블 이름.</param>
    /// <param name="errors">발견한 오류 전부.</param>
    public StaticTableLoadException(string tableName, IReadOnlyList<StaticTableError> errors)
        : base(Format(tableName, errors)) =>
        Errors = new ReadOnlyCollection<StaticTableError>([.. errors ?? []]);

    /// <summary>발견한 오류 전부. 첫 오류만이 아니다.</summary>
    public IReadOnlyList<StaticTableError> Errors { get; }

    private static string Format(string tableName, IReadOnlyList<StaticTableError>? errors)
    {
        if (errors is null || errors.Count == 0)
        {
            return $"테이블 '{tableName}' 로딩에 실패했다.";
        }

        StringBuilder builder = new();
        builder.Append(CultureInfo.InvariantCulture, $"테이블 '{tableName}' 로딩에 실패했다 — 오류 {errors.Count}건:");

        // 전부 보여 준다. 잘라내면 "고치고 다시 띄우기" 왕복이 다시 생긴다.
        foreach (StaticTableError error in errors)
        {
            builder.Append(CultureInfo.InvariantCulture, $"\n  - {error}");
        }

        return builder.ToString();
    }
}
