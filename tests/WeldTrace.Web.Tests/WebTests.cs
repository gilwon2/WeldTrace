using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using WeldTrace.Core.Domain;
using WeldTrace.Data;
using WeldTrace.Data.Tests;

namespace WeldTrace.Web.Tests;

public sealed class WebApp(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:WeldTrace", connectionString);
        builder.UseEnvironment("Development");
    }
}

public class WebTests
{
    private const string Eq = "LW-01";
    private static readonly DateTime Day1 = new(2026, 1, 5, 9, 0, 0);
    private static readonly DateTime Day2 = new(2026, 1, 6, 9, 0, 0);

    private static async Task SeedTwoDaysAsync(TestDatabase db)
    {
        var service = new IngestService(db.ConnectionString);
        var day1 = Csv.Module(Day1)
            .Concat(Csv.Module(Day1.AddMinutes(5)).Select(w => w with { RealPower = 0 }))
            .Concat(Csv.Module(Day1.AddMinutes(10)).Select(w => w.PageNo == 14 ? w with { SetPower = 35, RealPower = 681 } : w))
            .Concat(Csv.Module(Day1.AddMinutes(15)));
        var day2 = Csv.Module(Day2)
            .Concat(Csv.Module(Day2.AddMinutes(5)).Select(w => w.PageNo == 5 ? w with { RealPower = 1000 } : w));
        await service.IngestAsync(Csv.File(day1), "day1.csv", Eq);
        await service.IngestAsync(Csv.File(day2), "day2.csv", Eq);
    }

    [SqlFact]
    public async Task Empty_database_shows_empty_states()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var app = new WebApp(db.ConnectionString);
        var client = app.CreateClient();

        Assert.Contains("아직 수집된 데이터가 없습니다", await client.GetStringAsync("/"));
        Assert.Contains("가동일이 부족합니다", await client.GetStringAsync("/Drift"));
        Assert.Contains("조건에 맞는 모듈이 없습니다", await client.GetStringAsync("/Modules"));
        Assert.Contains("아직 수집 이력이 없습니다", await client.GetStringAsync("/Batches"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [SqlFact]
    public async Task Pages_show_judgments_and_trace()
    {
        await using var db = await TestDatabase.CreateAsync();
        await SeedTwoDaysAsync(db);
        await using var app = new WebApp(db.ConnectionString);
        var client = app.CreateClient();

        var home = await client.GetStringAsync("/");
        Assert.Contains("최근 불량 모듈", home);
        Assert.Contains("50.0%", home); // 최근 가동일(1/6) 불량률: 2개 중 1개

        var ngList = await client.GetStringAsync("/Modules?verdict=NG");
        Assert.Contains("출력 저하", ngList);
        Assert.Contains("미출력", ngList);

        var ng = (await new QueryService(db.ConnectionString).ModulesAsync(Eq, verdict: "NG"))
            .Single(m => m.PrimaryFault == "POWER_DROP");
        var detail = await client.GetStringAsync($"/Modules/{ng.ModuleId}");
        Assert.Contains($"모듈 #{ng.ModuleId}", detail);
        Assert.Equal(1, CountOccurrences(detail, "class=\"row-ng\""));
        Assert.Contains("0.5886", detail); // 1000 / 1699

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Modules/999999")).StatusCode);
        Assert.Contains("이슈가 없습니다", await client.GetStringAsync($"/Batches?id={ng.ClosedByBatch}"));
    }

    [SqlFact]
    public async Task Api_returns_daily_quality_trace_and_factorylens_csv()
    {
        await using var db = await TestDatabase.CreateAsync();
        await SeedTwoDaysAsync(db);
        await using var app = new WebApp(db.ConnectionString);
        var client = app.CreateClient();

        using var daily = JsonDocument.Parse(await client.GetStringAsync("/api/quality/daily"));
        Assert.Equal(2, daily.RootElement.GetArrayLength());
        Assert.Equal(0.25, daily.RootElement[0].GetProperty("ngRate").GetDouble());

        Assert.Equal(
            "lot_id,defect_type,defect_qty\n" +
            "L20260105-LASERWELD-BM,NO_OUTPUT,1\n" +
            "L20260106-LASERWELD-BM,POWER_DROP,1\n",
            await client.GetStringAsync("/api/export/factorylens/defects.csv"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/export/factorylens/other.csv")).StatusCode);

        var ng = (await new QueryService(db.ConnectionString).ModulesAsync(Eq, verdict: "NG")).First();
        using var trace = JsonDocument.Parse(await client.GetStringAsync($"/api/modules/{ng.ModuleId}"));
        Assert.Equal(39, trace.RootElement.GetProperty("steps").GetArrayLength());

        using var drift = JsonDocument.Parse(await client.GetStringAsync("/api/drift"));
        Assert.Equal("insufficient-data", drift.RootElement.GetProperty("status").GetString());
    }

    [SqlFact]
    public async Task Drift_page_and_api_flag_a_drop_after_baseline()
    {
        // 25가동일, 하루 모듈 1개. 처음 15일은 공칭 ±0.1% 교대, 이후 10일은 공칭의 99%
        await using var db = await TestDatabase.CreateAsync();
        var program = WeldProgram.BatteryModuleDefault();
        var welds = new List<WeldRecord>();
        for (var day = 0; day < 25; day++)
        {
            var factor = day < 15 ? (day % 2 == 0 ? 1.001 : 0.999) : 0.99;
            welds.AddRange(Csv.Module(Day1.AddDays(day)).Select(w =>
                w with { RealPower = (int)Math.Round(program.NominalForStep(w.PageNo) * factor) }));
        }
        await new IngestService(db.ConnectionString).IngestAsync(Csv.File(welds), "drift.csv", Eq);

        await using var app = new WebApp(db.ConnectionString);
        var client = app.CreateClient();

        using var drift = JsonDocument.Parse(await client.GetStringAsync("/api/drift"));
        Assert.Equal("alarm", drift.RootElement.GetProperty("status").GetString());
        Assert.Equal(25, drift.RootElement.GetProperty("points").GetArrayLength());
        Assert.StartsWith("2026-01-20", drift.RootElement.GetProperty("firstEwmaAlarm").GetString());

        var page = await client.GetStringAsync("/Drift");
        Assert.Contains("EWMA 관리도", page);
        Assert.Contains("경보", page);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
