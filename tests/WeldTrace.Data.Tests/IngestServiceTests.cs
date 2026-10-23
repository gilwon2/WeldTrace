using Dapper;
using WeldTrace.Core.Domain;

namespace WeldTrace.Data.Tests;

public class IngestServiceTests
{
    private const string Eq = "LW-01";
    private static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0);

    private sealed class ModuleRow
    {
        public int ModuleId { get; set; }
        public int WeldCount { get; set; }
        public bool IsComplete { get; set; }
        public string Verdict { get; set; } = "";
        public string? PrimaryFault { get; set; }
        public double? MeanPowerRatio { get; set; }
    }

    private static async Task<List<ModuleRow>> ModulesAsync(TestDatabase db)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<ModuleRow>(
            "SELECT module_id AS ModuleId, weld_count AS WeldCount, is_complete AS IsComplete, verdict AS Verdict, " +
            "primary_fault AS PrimaryFault, mean_power_ratio AS MeanPowerRatio FROM dbo.module ORDER BY start_time")).ToList();
    }

    private static async Task<int> ScalarAsync(TestDatabase db, string sql)
    {
        await using var c = await db.OpenAsync();
        return await c.QuerySingleAsync<int>(sql);
    }

    [SqlFact]
    public async Task Loads_valid_rows_closes_complete_modules_and_keeps_issues()
    {
        await using var db = await TestDatabase.CreateAsync();
        var a = Csv.Module(T0);
        var b = Csv.Module(T0.AddMinutes(5))
            .Select(w => w.PageNo == 5 ? w with { RealPower = 1000 } : w).ToList();
        // 데이터 80줄 = 정상 모듈 39 + 출력 저하 모듈 39 + 중복(D1) 1 + 형식 오류(P3) 1
        var file = Csv.File(a.Concat(b),
            Csv.Line(a[0]),
            "1,250,241.1,abc,1000,100,82,1154, 2026-01-05 10:00:00.000");

        var report = await new IngestService(db.ConnectionString).IngestAsync(file, "day1.csv", Eq);

        Assert.Equal(IngestStatus.Succeeded, report.Status);
        Assert.Equal((80, 78, 2, 0, 2), (report.RowsRead, report.RowsLoaded, report.RowsRejected,
            report.RowsAlreadyLoaded, report.ModulesClosed));
        Assert.Equal(new[] { "D1", "P3" }, report.Issues.Select(i => i.Code).OrderBy(c => c));

        var modules = await ModulesAsync(db);
        Assert.Equal(new[] { "OK", "NG" }, modules.Select(m => m.Verdict));
        Assert.Equal("POWER_DROP", modules[1].PrimaryFault);
        Assert.Equal(1.0, modules[0].MeanPowerRatio!.Value, 9);

        Assert.Equal(2, await ScalarAsync(db, "SELECT COUNT(*) FROM dbo.data_issue"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) FROM dbo.weld WHERE module_id IS NULL"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) FROM dbo.weld_fault WHERE fault_code = 'POWER_DROP'"));

        await using var c = await db.OpenAsync();
        // 1000 / 1699 = 0.588581...
        var ratio = await c.QuerySingleAsync<double>(
            "SELECT power_ratio FROM dbo.weld WHERE real_power = 1000");
        Assert.Equal(0.588582, ratio, 6);
    }

    [SqlFact]
    public async Task Same_file_twice_is_skipped_K1()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new IngestService(db.ConnectionString);
        var file = Csv.File(Csv.Module(T0));

        var first = await service.IngestAsync(file, "day1.csv", Eq);
        var second = await service.IngestAsync(file, "day1-copy.csv", Eq);

        Assert.Equal(IngestStatus.Succeeded, first.Status);
        Assert.Equal(IngestStatus.SkippedDuplicateFile, second.Status);
        Assert.Equal(first.BatchId, second.BatchId);
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) FROM dbo.ingest_batch"));
        Assert.Equal(39, await ScalarAsync(db, "SELECT COUNT(*) FROM dbo.weld"));
    }

    [SqlFact]
    public async Task Overlapping_file_loads_only_new_rows_K2()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new IngestService(db.ConnectionString);
        var a = Csv.Module(T0);
        var b = Csv.Module(T0.AddMinutes(5));

        await service.IngestAsync(Csv.File(a), "part1.csv", Eq);
        var report = await service.IngestAsync(Csv.File(a.Concat(b)), "part1+2.csv", Eq);

        Assert.Equal((78, 39, 39, 1), (report.RowsRead, report.RowsLoaded, report.RowsAlreadyLoaded, report.ModulesClosed));
        Assert.Equal(78, await ScalarAsync(db, "SELECT COUNT(*) FROM dbo.weld"));
        Assert.Equal(2, (await ModulesAsync(db)).Count);
    }

    [SqlFact]
    public async Task Module_cut_at_file_boundary_waits_then_closes_whole()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new IngestService(db.ConnectionString);

        var first = await service.IngestAsync(Csv.File(Csv.Module(T0, 1, 20)), "a.csv", Eq);
        Assert.Equal(0, first.ModulesClosed);
        Assert.Equal(20, await ScalarAsync(db, "SELECT COUNT(*) FROM dbo.weld WHERE module_id IS NULL"));

        var second = await service.IngestAsync(Csv.File(Csv.Module(T0, 21, 39)), "b.csv", Eq);
        Assert.Equal(1, second.ModulesClosed);

        var module = Assert.Single(await ModulesAsync(db));
        Assert.Equal((39, true, "OK"), (module.WeldCount, module.IsComplete, module.Verdict));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) FROM dbo.weld WHERE module_id IS NULL"));
    }

    [SqlFact]
    public async Task Interrupted_module_is_closed_as_hold_when_next_module_starts()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new IngestService(db.ConnectionString);

        await service.IngestAsync(Csv.File(Csv.Module(T0, 1, 20)), "a.csv", Eq);
        var report = await service.IngestAsync(Csv.File(Csv.Module(T0.AddMinutes(10))), "b.csv", Eq);

        Assert.Equal(2, report.ModulesClosed);
        var modules = await ModulesAsync(db);
        Assert.Equal(new[] { ("HOLD", 20, false), ("OK", 39, true) },
            modules.Select(m => (m.Verdict, m.WeldCount, m.IsComplete)));
        Assert.Null(modules[0].MeanPowerRatio);
    }

    [SqlFact]
    public async Task Recipe_deviation_is_traceable_to_the_weld()
    {
        await using var db = await TestDatabase.CreateAsync();
        // 원본 03일 사례: 스텝 14 설정 35, 실측 681W → 경고(불량 아님)
        var welds = Csv.Module(T0).Select(w => w.PageNo == 14 ? w with { SetPower = 35, RealPower = 681 } : w);

        await new IngestService(db.ConnectionString).IngestAsync(Csv.File(welds), "dev.csv", Eq);

        var module = Assert.Single(await ModulesAsync(db));
        Assert.Equal(("WARNING", "RECIPE_DEVIATION"), (module.Verdict, module.PrimaryFault));

        await using var c = await db.OpenAsync();
        var trace = await c.QuerySingleAsync<(int SetPower, int ExpectedSetPower, string Faults)>(
            "SELECT set_power, expected_set_power, faults FROM dbo.vw_module_trace WHERE page_no = 14");
        Assert.Equal((35, 38, "RECIPE_DEVIATION"), trace);
    }

    [SqlFact]
    public async Task Daily_quality_view_counts_modules_per_day()
    {
        await using var db = await TestDatabase.CreateAsync();
        var day1 = Csv.Module(T0)
            .Concat(Csv.Module(T0.AddMinutes(5)).Select(w => w with { RealPower = 0 }))
            .Concat(Csv.Module(T0.AddMinutes(10)))
            .Concat(Csv.Module(T0.AddMinutes(15)));

        await new IngestService(db.ConnectionString).IngestAsync(Csv.File(day1), "day1.csv", Eq);

        await using var c = await db.OpenAsync();
        var row = await c.QuerySingleAsync<(int Modules, int Ng, int Warning, double NgRate)>(
            "SELECT modules, ng_modules, warning_modules, ng_rate FROM dbo.vw_daily_quality");
        // 4개 중 미출력 1개 → 불량률 0.25
        Assert.Equal((4, 1, 0, 0.25), row);
    }

    [SqlFact]
    public async Task File_without_required_columns_is_recorded_as_failed()
    {
        await using var db = await TestDatabase.CreateAsync();
        var bad = System.Text.Encoding.UTF8.GetBytes("PageNo,RealPower\n1,1699\n");

        var report = await new IngestService(db.ConnectionString).IngestAsync(bad, "bad.csv", Eq);

        Assert.Equal(IngestStatus.Failed, report.Status);
        Assert.Equal("F1", Assert.Single(report.Issues).Code);
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) FROM dbo.ingest_batch WHERE status = 'FAILED'"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) FROM dbo.weld"));
    }

    [SqlFact]
    public async Task Gzip_sample_file_loads()
    {
        await using var db = await TestDatabase.CreateAsync();
        var raw = Csv.File(Csv.Module(T0));
        using var ms = new MemoryStream();
        await using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            await gz.WriteAsync(raw);

        var report = await new IngestService(db.ConnectionString).IngestAsync(ms.ToArray(), "day.csv.gz", Eq);

        Assert.Equal((IngestStatus.Succeeded, 39, 1), (report.Status, report.RowsLoaded, report.ModulesClosed));
    }
}
