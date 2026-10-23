using System.Diagnostics;
using Dapper;

namespace WeldTrace.Data.Tests;

/// <summary>
/// 60일치 합성 샘플(약 10만 행) 전체 적재. 기대값은 tools/reference/expected_counts.py가
/// C# 구현과 독립적으로 계산한 값이다 (교차 검증).
///   data_lines=106705 p2=1 d1=1 accepted=106703
///   modules=2736 verdicts={OK: 2676, NG: 59, HOLD: 1} ng_by_fault={POWER_DROP: 49, NO_OUTPUT: 10}
/// </summary>
public class SampleScaleTests
{
    private static string SampleFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "data", "sample")))
            dir = dir.Parent;
        if (dir is null)
            throw new DirectoryNotFoundException("data/sample 폴더를 찾지 못했습니다.");
        return Directory.GetFiles(Path.Combine(dir.FullName, "data", "sample"), "welding_*.csv.gz").Single();
    }

    private sealed class VerdictCount
    {
        public string Verdict { get; set; } = "";
        public string? PrimaryFault { get; set; }
        public int N { get; set; }
    }

    [SqlFact]
    public async Task Full_sample_matches_independent_reference()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new IngestService(db.ConnectionString);

        var sw = Stopwatch.StartNew();
        var report = await service.IngestFileAsync(SampleFile(), "LW-01");
        sw.Stop();

        Assert.Equal(IngestStatus.Succeeded, report.Status);
        Assert.Equal((106705, 106703, 2, 0), (report.RowsRead, report.RowsLoaded, report.RowsRejected, report.RowsAlreadyLoaded));
        Assert.Equal(2736, report.ModulesClosed);
        Assert.Equal(new[] { "D1", "P2", "T1" }, report.Issues.Select(i => i.Code).OrderBy(c => c));

        await using var c = await db.OpenAsync();
        var counts = (await c.QueryAsync<VerdictCount>(
            "SELECT verdict AS Verdict, primary_fault AS PrimaryFault, COUNT(*) AS N FROM dbo.module " +
            "GROUP BY verdict, primary_fault")).ToDictionary(x => (x.Verdict, x.PrimaryFault), x => x.N);

        Assert.Equal(2676, counts[("OK", null)]);
        Assert.Equal(49, counts[("NG", "POWER_DROP")]);
        Assert.Equal(10, counts[("NG", "NO_OUTPUT")]);
        Assert.Equal(1, counts.Where(kv => kv.Key.Verdict == "HOLD").Sum(kv => kv.Value));
        Assert.Equal(4, counts.Count);

        // 같은 파일을 다시 넣어도 아무것도 바뀌지 않는다 (K1)
        var again = await service.IngestFileAsync(SampleFile(), "LW-01");
        Assert.Equal(IngestStatus.SkippedDuplicateFile, again.Status);

        var perfOut = Environment.GetEnvironmentVariable("WELDTRACE_PERF_OUT");
        if (!string.IsNullOrEmpty(perfOut))
        {
            await File.AppendAllTextAsync(perfOut,
                $"Sample ingest: {report.RowsLoaded:N0} rows, {report.ModulesClosed:N0} modules in {sw.Elapsed.TotalSeconds:F1}s\n");
        }
    }
}
