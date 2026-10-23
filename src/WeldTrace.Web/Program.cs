using System.Text;
using WeldTrace.Data;
using WeldTrace.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSingleton(sp =>
{
    var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("WeldTrace")
                           ?? throw new InvalidOperationException("ConnectionStrings:WeldTrace 설정이 없습니다.");
    return new QueryService(connectionString);
});
builder.Services.AddSingleton<SiteContext>();
builder.Services.AddHostedService<DatabaseStartup>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();

// ---- API: 화면과 같은 데이터를 JSON·CSV로 (다른 시스템 연동, FactoryLens 업로드용) ----
var api = app.MapGroup("/api");

api.MapGet("/overview", async (QueryService q, SiteContext site, CancellationToken ct) =>
    Results.Ok(await q.OverviewAsync(site.EquipmentId, ct)));

api.MapGet("/quality/daily", async (QueryService q, SiteContext site, CancellationToken ct) =>
    Results.Ok(await q.DailyQualityAsync(site.EquipmentId, ct)));

api.MapGet("/drift", async (QueryService q, SiteContext site, CancellationToken ct) =>
{
    var report = await DriftView.AnalyzeAsync(q, site.EquipmentId, ct);
    return report is null
        ? Results.Ok(new { status = "insufficient-data", minimumDays = DriftView.MinimumDays })
        : Results.Ok(new
        {
            status = report.AlarmNow ? "alarm" : "normal",
            baseline = new { report.Baseline.Mean, report.Baseline.StdDev, days = report.Baseline.Count },
            report.FirstEwmaAlarm,
            report.FirstCusumAlarm,
            slopePerDay = report.SlopePerDay,
            warning = report.Warning,
            failure = report.Failure,
            points = report.Points,
        });
});

api.MapGet("/modules/{id:int}", async (int id, QueryService q, CancellationToken ct) =>
    await q.ModuleTraceAsync(id, ct) is { } trace ? Results.Ok(trace) : Results.NotFound());

api.MapGet("/export/factorylens/{kind}.csv", async (string kind, DateTime? from, DateTime? to,
    QueryService q, SiteContext site, CancellationToken ct) =>
{
    var files = await q.FactoryLensAsync(site.EquipmentId, from, to, ct);
    var body = kind switch
    {
        "production" => files.ProductionCsv,
        "defects" => files.DefectsCsv,
        _ => null,
    };
    return body is null
        ? Results.NotFound()
        : Results.File(Encoding.UTF8.GetBytes(body), "text/csv; charset=utf-8", $"weldtrace_{kind}.csv");
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

/// <summary>WebApplicationFactory 통합 테스트에서 진입점을 참조하기 위한 선언.</summary>
public partial class Program
{
}
