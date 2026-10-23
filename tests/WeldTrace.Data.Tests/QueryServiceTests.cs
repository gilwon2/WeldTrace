namespace WeldTrace.Data.Tests;

public class QueryServiceTests
{
    private const string Eq = "LW-01";
    private static readonly DateTime Day1 = new(2026, 1, 5, 9, 0, 0);
    private static readonly DateTime Day2 = new(2026, 1, 6, 9, 0, 0);

    /// <summary>
    /// 1일차 4모듈: 정상, 미출력(NG), 설정 이탈(경고), 정상
    /// 2일차 2모듈: 정상, 스텝 5 출력 저하(NG)
    /// </summary>
    private static async Task<TestDatabase> SeedAsync()
    {
        var db = await TestDatabase.CreateAsync();
        var service = new IngestService(db.ConnectionString);
        var day1 = Csv.Module(Day1)
            .Concat(Csv.Module(Day1.AddMinutes(5)).Select(w => w with { RealPower = 0 }))
            .Concat(Csv.Module(Day1.AddMinutes(10)).Select(w => w.PageNo == 14 ? w with { SetPower = 35, RealPower = 681 } : w))
            .Concat(Csv.Module(Day1.AddMinutes(15)));
        var day2 = Csv.Module(Day2)
            .Concat(Csv.Module(Day2.AddMinutes(5)).Select(w => w.PageNo == 5 ? w with { RealPower = 1000 } : w));
        await service.IngestAsync(Csv.File(day1), "day1.csv", Eq);
        await service.IngestAsync(Csv.File(day2), "day2.csv", Eq);
        return db;
    }

    [SqlFact]
    public async Task Overview_and_daily_quality_match_hand_counts()
    {
        await using var db = await SeedAsync();
        var q = new QueryService(db.ConnectionString);

        var o = await q.OverviewAsync(Eq);
        Assert.Equal((6, 2, 1, 0, 234L, 0), (o.Modules, o.NgModules, o.WarningModules, o.HoldModules, o.Welds, o.OpenWelds));
        Assert.Equal(Day1, o.FirstWeld);

        var daily = await q.DailyQualityAsync(Eq);
        Assert.Equal(new[]
        {
            (new DateTime(2026, 1, 5), 4, 1, 1, 0.25),
            (new DateTime(2026, 1, 6), 2, 1, 0, 0.5),
        }, daily.Select(d => (d.WorkDate, d.Modules, d.NgModules, d.WarningModules, d.NgRate)));
    }

    [SqlFact]
    public async Task Power_series_excludes_modules_without_clean_welds()
    {
        await using var db = await SeedAsync();
        var series = await new QueryService(db.ConnectionString).PowerSeriesAsync(Eq);

        // 미출력 모듈은 정상 용접이 하나도 없어 평균이 없다 → 6개 중 5개
        Assert.Equal(5, series.Count);
        Assert.All(series, p => Assert.Equal(1.0, p.Value, 9));
        Assert.Equal(series.OrderBy(p => p.Time), series);
    }

    [SqlFact]
    public async Task Modules_can_be_filtered_by_day_and_verdict()
    {
        await using var db = await SeedAsync();
        var q = new QueryService(db.ConnectionString);

        Assert.Equal(4, (await q.ModulesAsync(Eq, workDate: new DateTime(2026, 1, 5))).Count);
        var ng = Assert.Single(await q.ModulesAsync(Eq, workDate: new DateTime(2026, 1, 6), verdict: "NG"));
        Assert.Equal("POWER_DROP", ng.PrimaryFault);
        Assert.Equal(new[] { new DateTime(2026, 1, 6), new DateTime(2026, 1, 5) }, await q.WorkDatesAsync(Eq));
    }

    [SqlFact]
    public async Task Module_trace_shows_every_step_with_its_fault()
    {
        await using var db = await SeedAsync();
        var q = new QueryService(db.ConnectionString);
        var ng = (await q.ModulesAsync(Eq, verdict: "NG")).Single(m => m.PrimaryFault == "POWER_DROP");

        var trace = (await q.ModuleTraceAsync(ng.ModuleId))!;

        Assert.Equal(39, trace.Steps.Count);
        Assert.Equal(Enumerable.Range(1, 39), trace.Steps.Select(s => s.PageNo));
        var bad = Assert.Single(trace.Steps, s => s.Faults is not null);
        Assert.Equal((5, 82, 82, 1000, "POWER_DROP"), (bad.PageNo, bad.SetPower, bad.ExpectedSetPower, bad.RealPower, bad.Faults));
        Assert.Equal(0.588582, bad.PowerRatio!.Value, 6);
        Assert.Null(await q.ModuleTraceAsync(999999));
    }

    [SqlFact]
    public async Task Batches_are_listed_newest_first_with_issue_counts()
    {
        await using var db = await SeedAsync();
        var batches = await new QueryService(db.ConnectionString).RecentBatchesAsync();

        Assert.Equal(new[] { "day2.csv", "day1.csv" }, batches.Select(b => b.SourceName));
        Assert.All(batches, b => Assert.Equal(("SUCCEEDED", 0), (b.Status, b.Issues)));
        Assert.Equal((78, 2), (batches[0].RowsLoaded, batches[0].ModulesClosed));
    }

    [SqlFact]
    public async Task FactoryLens_export_matches_hand_written_csv()
    {
        await using var db = await SeedAsync();
        var files = await new QueryService(db.ConnectionString).FactoryLensAsync(Eq);

        Assert.Equal(
            "lot_id,work_date,process,product,produced_qty\n" +
            "L20260105-LASERWELD-BM,2026-01-05,LASER_WELD,BATTERY_MODULE,4\n" +
            "L20260106-LASERWELD-BM,2026-01-06,LASER_WELD,BATTERY_MODULE,2\n",
            files.ProductionCsv);
        Assert.Equal(
            "lot_id,defect_type,defect_qty\n" +
            "L20260105-LASERWELD-BM,NO_OUTPUT,1\n" +
            "L20260106-LASERWELD-BM,POWER_DROP,1\n",
            files.DefectsCsv);

        var onlyDay2 = await new QueryService(db.ConnectionString).FactoryLensAsync(Eq, from: new DateTime(2026, 1, 6));
        Assert.DoesNotContain("2026-01-05", onlyDay2.ProductionCsv);
    }
}
