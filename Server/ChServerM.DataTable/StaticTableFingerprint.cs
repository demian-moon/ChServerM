using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace ChServerM.DataTable;

/// <summary>
/// 표(또는 묶음)의 내용을 요약한 128비트 지문 — <b>서버와 클라이언트가 같은 데이터를
/// 보고 있는지</b>를 값 하나로 대조하기 위한 것.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 밸런스 표가 어긋난 채로 접속하면 증상이 <b>한참 뒤에 엉뚱한 모습</b>으로
/// 나타난다 — 클라가 보여 준 데미지와 서버가 계산한 데미지가 다르고, 그것을 재현하려면
/// 양쪽 데이터 파일을 대조해야 한다. 레거시는 이 문제를 **서버 표를 클라에 그대로 전송**해
/// 원천 차단했고(docs/legacy/11-data-table, 승계 판정 🟢), 그 전송을 하지 않는 배포에서도
/// <b>최소한 어긋났다는 사실은 접속 시점에</b> 알아야 한다.
/// </para>
///
/// <para>
/// <b>⚠ 파일이 아니라 파싱 결과를 지문으로 만든다.</b> 파일 바이트를 해싱하면 주석 한 줄,
/// 줄 끝 공백, CRLF/LF 차이가 전부 불일치가 된다. CSV 리더는 주석을 <b>의도적으로</b>
/// 허용하는데("이 값은 왜 이런가" 를 적을 자리가 필요하다), 그 주석을 고쳤다고 전 클라이언트가
/// 거부되면 기능이 스스로를 무용지물로 만든다. 그래서 지문의 입력은 <b>스키마와 파싱된 값</b>이다.
/// </para>
///
/// <para>
/// <b>행 순서는 지문에 포함된다.</b> 행 번호가 곧 참조의 목적지이기 때문이다
/// (<see cref="StaticTable.GetReference"/>). 같은 행을 순서만 바꿔 적은 표는 <b>다른 표</b>다.
/// 반대로 <b>묶음 안에서 표의 등록 순서는 포함되지 않는다</b> — 표 이름으로 정렬해 결합하므로
/// <see cref="StaticTableSetBuilder"/> 에 넣은 순서가 지문을 바꾸지 않는다.
/// </para>
///
/// <para>
/// <b>⚠ 왜 <c>string.GetHashCode</c> 가 아닌가.</b> .NET 의 문자열 해시는 <b>프로세스마다
/// 시드가 다르다</b>(해시 DoS 방어). 같은 데이터가 실행할 때마다 다른 값이 되므로 대조에
/// 쓸 수 없다. <see cref="XxHash128"/> 는 입력이 같으면 프로세스·플랫폼·런타임 버전을
/// 가로질러 같은 값을 낸다.
/// </para>
///
/// <para>
/// <b>암호학적 무결성이 아니다.</b> 이것은 <b>사고를 막는 장치</b>이지 <b>공격을 막는
/// 장치</b>가 아니다. 지문을 위조한 클라이언트를 막고 싶다면 그것은 인증·서명의 문제이고,
/// 128비트 비암호 해시로는 답이 되지 않는다.
/// </para>
///
/// <para><b>스레드 규약.</b> 불변 값 타입이다.</para>
/// </remarks>
public readonly struct StaticTableFingerprint : IEquatable<StaticTableFingerprint>
{
    /// <summary>지문의 바이트 길이.</summary>
    public const int ByteLength = 16;

    /// <summary>상위 64비트와 하위 64비트로 만든다.</summary>
    /// <param name="high">상위 64비트.</param>
    /// <param name="low">하위 64비트.</param>
    public StaticTableFingerprint(ulong high, ulong low)
    {
        High = high;
        Low = low;
    }

    /// <summary>상위 64비트.</summary>
    public ulong High { get; }

    /// <summary>하위 64비트.</summary>
    public ulong Low { get; }

    /// <summary>지문을 16바이트로 쓴다(리틀 엔디언, 하위 → 상위).</summary>
    /// <param name="destination"><see cref="ByteLength"/> 바이트 이상.</param>
    /// <exception cref="ArgumentException">대상이 짧다.</exception>
    /// <remarks>
    /// 와이어로 보낼 때 쓴다. 바이트 순서를 여기서 고정해 두면 전송 축이 무엇이든
    /// 같은 표현이 된다.
    /// </remarks>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < ByteLength)
        {
            throw new ArgumentException($"대상은 {ByteLength} 바이트 이상이어야 한다.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination, Low);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], High);
    }

    /// <inheritdoc/>
    public bool Equals(StaticTableFingerprint other) => High == other.High && Low == other.Low;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is StaticTableFingerprint other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(High, Low);

    /// <summary>32자리 16진 표현. 로그와 배포 스크립트에서 눈으로 대조할 수 있어야 한다.</summary>
    /// <returns>16진 문자열.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{High:x16}{Low:x16}");

    /// <summary>두 지문이 같은지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    /// <returns>같으면 <see langword="true"/>.</returns>
    public static bool operator ==(StaticTableFingerprint left, StaticTableFingerprint right) =>
        left.Equals(right);

    /// <summary>두 지문이 다른지 비교한다.</summary>
    /// <param name="left">왼쪽 값.</param>
    /// <param name="right">오른쪽 값.</param>
    /// <returns>다르면 <see langword="true"/>.</returns>
    public static bool operator !=(StaticTableFingerprint left, StaticTableFingerprint right) =>
        !left.Equals(right);
}

/// <summary>지문을 계산하는 내부 도우미.</summary>
/// <remarks>
/// <para>
/// <b>모호함이 없도록 길이를 함께 먹인다.</b> 문자열을 그냥 이어 붙이면
/// <c>"ab" + "c"</c> 와 <c>"a" + "bc"</c> 가 같은 입력이 된다 — 열 이름이나 값이 그렇게
/// 겹치면 <b>다른 표가 같은 지문</b>을 갖는다. 모든 가변 길이 항목 앞에 길이를 넣는다.
/// </para>
/// <para>
/// <b>null 과 빈 문자열을 구분한다.</b> 선택 열의 빈 칸(<see langword="null"/>)과 빈 문자열은
/// 다른 값이므로 길이 <c>-1</c> 로 구분해 먹인다.
/// </para>
/// </remarks>
internal static class StaticTableFingerprintCalculator
{
    /// <summary>표 하나의 지문 — 스키마와 <b>파싱된 값 전부</b>.</summary>
    internal static StaticTableFingerprint Compute(StaticTable table)
    {
        XxHash128 hash = new();

        AppendSchema(hash, table.Schema);
        AppendInt64(hash, table.RowCount);

        IReadOnlyList<StaticTableColumn> columns = table.Schema.Columns;

        for (int row = 0; row < table.RowCount; row++)
        {
            for (int ordinal = 0; ordinal < columns.Count; ordinal++)
            {
                switch (columns[ordinal].Type)
                {
                    case StaticTableColumnType.String:
                        AppendString(hash, table.GetString(row, ordinal));
                        break;

                    case StaticTableColumnType.Int32:
                    case StaticTableColumnType.Int64:
                        AppendInt64(hash, table.GetInt64(row, ordinal));
                        break;

                    case StaticTableColumnType.Double:
                        AppendDouble(hash, table.GetDouble(row, ordinal));
                        break;

                    case StaticTableColumnType.Boolean:
                        AppendBoolean(hash, table.GetBoolean(row, ordinal));
                        break;

                    default:
                        break;
                }
            }
        }

        return Finish(hash);
    }

    /// <summary>묶음의 지문 — 표 이름으로 <b>정렬해</b> 결합한다(등록 순서 무관).</summary>
    internal static StaticTableFingerprint Combine(IReadOnlyDictionary<string, StaticTable> tables)
    {
        string[] names = new string[tables.Count];
        int index = 0;
        foreach (string name in tables.Keys)
        {
            names[index++] = name;
        }

        Array.Sort(names, StringComparer.Ordinal);

        XxHash128 hash = new();
        AppendInt64(hash, names.Length);

        foreach (string name in names)
        {
            AppendString(hash, name);

            StaticTableFingerprint table = tables[name].Fingerprint;
            AppendInt64(hash, unchecked((long)table.High));
            AppendInt64(hash, unchecked((long)table.Low));
        }

        return Finish(hash);
    }

    private static void AppendSchema(XxHash128 hash, StaticTableSchema schema)
    {
        AppendString(hash, schema.Name);
        AppendString(hash, schema.KeyColumnName);
        AppendInt64(hash, schema.Columns.Count);

        foreach (StaticTableColumn column in schema.Columns)
        {
            AppendString(hash, column.Name);
            AppendInt64(hash, (long)column.Type);
            AppendBoolean(hash, column.Required);
            AppendString(hash, column.ReferencesTable);

            // 제약도 지문에 넣는다 — 같은 값이라도 **허용 범위가 다르면 다른 계약**이고,
            // 클라이언트가 그 범위를 근거로 UI 를 만들 수 있다.
            AppendOptionalInt64(hash, column.MinimumInteger);
            AppendOptionalInt64(hash, column.MaximumInteger);
            AppendOptionalDouble(hash, column.MinimumReal);
            AppendOptionalDouble(hash, column.MaximumReal);
        }
    }

    private static void AppendString(XxHash128 hash, string? value)
    {
        if (value is null)
        {
            // ⚠ null 과 "" 를 구분한다. 길이 -1 이 null 이다.
            AppendInt64(hash, -1);
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(value);
        AppendInt64(hash, byteCount);

        // 표의 값은 대개 짧다. 상한을 두고 넘으면 힙으로 넘긴다.
        if (byteCount <= 256)
        {
            Span<byte> buffer = stackalloc byte[256];
            int written = Encoding.UTF8.GetBytes(value, buffer);
            hash.Append(buffer[..written]);
            return;
        }

        hash.Append(Encoding.UTF8.GetBytes(value));
    }

    private static void AppendInt64(XxHash128 hash, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        hash.Append(buffer);
    }

    /// <summary>실수는 <b>비트 패턴</b>으로 먹인다 — 문자열 변환은 컬처·포맷에 흔들린다.</summary>
    private static void AppendDouble(XxHash128 hash, double value) =>
        AppendInt64(hash, BitConverter.DoubleToInt64Bits(value));

    private static void AppendBoolean(XxHash128 hash, bool value)
    {
        Span<byte> buffer = [value ? (byte)1 : (byte)0];
        hash.Append(buffer);
    }

    /// <summary>"제약 없음" 과 "제약 값이 0" 을 구분해 먹인다.</summary>
    private static void AppendOptionalInt64(XxHash128 hash, long? value)
    {
        AppendBoolean(hash, value.HasValue);
        AppendInt64(hash, value ?? 0);
    }

    private static void AppendOptionalDouble(XxHash128 hash, double? value)
    {
        AppendBoolean(hash, value.HasValue);
        AppendDouble(hash, value ?? 0);
    }

    private static StaticTableFingerprint Finish(XxHash128 hash)
    {
        Span<byte> digest = stackalloc byte[StaticTableFingerprint.ByteLength];
        hash.GetCurrentHash(digest);

        return new StaticTableFingerprint(
            BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest));
    }
}
