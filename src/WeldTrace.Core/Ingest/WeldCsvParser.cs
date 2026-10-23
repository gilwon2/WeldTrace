using System.Globalization;
using WeldTrace.Core.Domain;

namespace WeldTrace.Core.Ingest;

/// <summary>파일 전체를 거부해야 하는 형식 오류 (헤더 누락 등).</summary>
public sealed class WeldFileFormatException(string message) : Exception(message);

/// <summary>원본 CSV 줄 번호를 함께 보관한 파싱 결과 행.</summary>
public sealed record ParsedWeld(int Line, WeldRecord Record);

public sealed record ParseResult(IReadOnlyList<ParsedWeld> Rows, IReadOnlyList<DataIssue> Issues, int DataLineCount);

/// <summary>
/// 용접기 CSV를 읽는다. 원본은 컬럼명과 WorkingTime 값 앞에 공백이 붙어 있어 모든 값을 Trim한다.
/// 규칙 코드:
///   F1 필수 컬럼 누락 (파일 거부)
///   P1 열 개수 불일치, P2 필수값 결측, P3 숫자·날짜 형식 오류 (해당 행 제외)
/// </summary>
public static class WeldCsvParser
{
    public static readonly string[] RequiredColumns =
    [
        "PageNo", "Speed", "Length", "RealPower", "SetFrequency", "SetDuty", "SetPower", "GateOnTime", "WorkingTime",
    ];

    private static readonly string[] TimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss.ff",
        "yyyy-MM-dd HH:mm:ss.f",
        "yyyy-MM-dd HH:mm:ss",
    ];

    public static ParseResult Parse(TextReader reader)
    {
        var header = reader.ReadLine() ?? throw new WeldFileFormatException("빈 파일입니다.");
        var columns = header.Split(',').Select(c => c.Trim().TrimStart('﻿')).ToArray();

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Length; i++)
        {
            if (columns[i].Length > 0 && !index.ContainsKey(columns[i]))
                index[columns[i]] = i;
        }

        var missing = RequiredColumns.Where(c => !index.ContainsKey(c)).ToList();
        if (missing.Count > 0)
            throw new WeldFileFormatException($"[F1] 필수 컬럼이 없습니다: {string.Join(", ", missing)}");

        var rows = new List<ParsedWeld>();
        var issues = new List<DataIssue>();
        var lineNo = 1;
        var dataLines = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            dataLines++;

            var fields = line.Split(',');
            if (fields.Length != columns.Length)
            {
                issues.Add(new DataIssue(lineNo, "P1", IssueLevel.Error,
                    $"열 개수가 {fields.Length}개입니다 (헤더 기준 {columns.Length}개)."));
                continue;
            }

            string Field(string name) => fields[index[name]].Trim();

            var blank = RequiredColumns.Where(c => Field(c).Length == 0).ToList();
            if (blank.Count > 0)
            {
                issues.Add(new DataIssue(lineNo, "P2", IssueLevel.Error,
                    $"필수값이 비어 있습니다: {string.Join(", ", blank)}"));
                continue;
            }

            var bad = new List<string>();
            int Int(string name)
            {
                if (int.TryParse(Field(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
                bad.Add(name);
                return 0;
            }

            var pageNo = Int("PageNo");
            var speed = Int("Speed");
            var realPower = Int("RealPower");
            var setFrequency = Int("SetFrequency");
            var setDuty = Int("SetDuty");
            var setPower = Int("SetPower");
            var gateOnTime = Int("GateOnTime");

            if (!decimal.TryParse(Field("Length"), NumberStyles.Number, CultureInfo.InvariantCulture, out var length))
                bad.Add("Length");

            if (!DateTime.TryParseExact(Field("WorkingTime"), TimeFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var workingTime))
                bad.Add("WorkingTime");

            if (bad.Count > 0)
            {
                issues.Add(new DataIssue(lineNo, "P3", IssueLevel.Error,
                    $"형식이 올바르지 않습니다: {string.Join(", ", bad)}"));
                continue;
            }

            rows.Add(new ParsedWeld(lineNo, new WeldRecord(
                pageNo, speed, length, realPower, setFrequency, setDuty, setPower, gateOnTime, workingTime)));
        }

        return new ParseResult(rows, issues, dataLines);
    }

    public static ParseResult ParseFile(string path)
    {
        using var stream = File.OpenRead(path);
        Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress)
            : stream;
        using var reader = new StreamReader(input);
        return Parse(reader);
    }
}
