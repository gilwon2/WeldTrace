using System.Globalization;

namespace WeldTrace.Core.Ingest;

/// <summary>
/// 기록된 용접 CSV를 "시간이 흐르는 것처럼" 조금씩 내보낸다. 실제 설비 대신 쓰는 리플레이 원천.
/// 원본 줄을 한 글자도 바꾸지 않고 파일 순서 그대로 내보낸다. 그래야 중복·시간 역전 같은
/// 실제 데이터 문제가 수집 단계까지 그대로 전달되어 검증 규칙이 실제로 일하는지 볼 수 있다.
/// </summary>
public sealed class ReplaySource
{
    private readonly List<(DateTime Time, string Line)> _rows;
    private int _cursor;

    private ReplaySource(string header, List<(DateTime Time, string Line)> rows)
    {
        Header = header;
        _rows = rows;
    }

    public string Header { get; }
    public int TotalRows => _rows.Count;
    public int EmittedRows => _cursor;
    public bool Completed => _cursor >= _rows.Count;

    /// <summary>첫 행의 용접 시각. 리플레이 가상 시계의 시작점.</summary>
    public DateTime Start => _rows.Count > 0 ? _rows[0].Time : DateTime.MinValue;

    /// <summary>
    /// CSV를 읽는다. 시각을 읽을 수 없는 행(결측 등)은 직전 행의 시각을 따라 함께 내보낸다.
    /// </summary>
    public static ReplaySource Load(TextReader reader)
    {
        var header = reader.ReadLine() ?? throw new WeldFileFormatException("빈 파일입니다.");
        var columns = header.Split(',').Select(c => c.Trim().TrimStart('﻿')).ToList();
        var timeIndex = columns.FindIndex(c => c.Equals("WorkingTime", StringComparison.OrdinalIgnoreCase));
        if (timeIndex < 0)
            throw new WeldFileFormatException("[F1] WorkingTime 컬럼이 없습니다.");

        var rows = new List<(DateTime, string)>();
        var last = DateTime.MinValue;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var fields = line.Split(',');
            if (fields.Length > timeIndex && DateTime.TryParse(fields[timeIndex].Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var t))
                last = t;
            rows.Add((last, line));
        }

        return new ReplaySource(header, rows);
    }

    /// <summary>
    /// 가상 시각 now까지 발생한 행을 파일 순서대로 꺼낸다. now보다 늦은 행을 만나면 멈춘다.
    /// (시각이 거꾸로 간 행은 순서상 도달했을 때 함께 나간다.)
    /// </summary>
    public IReadOnlyList<string> TakeUntil(DateTime now)
    {
        var taken = new List<string>();
        while (_cursor < _rows.Count && _rows[_cursor].Time <= now)
        {
            taken.Add(_rows[_cursor].Line);
            _cursor++;
        }
        return taken;
    }

    /// <summary>
    /// 실제 경과 시간에 배속을 곱해 가상 시각을 구한다. 예: 배속 60이면 실제 1초 = 설비 1분.
    /// </summary>
    public DateTime VirtualNow(TimeSpan realElapsed, double speed) =>
        Start + TimeSpan.FromTicks((long)(realElapsed.Ticks * speed));
}
