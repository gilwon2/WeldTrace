using System.Globalization;
using WeldTrace.Core.Spc;
using WeldTrace.Data;

namespace WeldTrace.Web;

/// <summary>화면 공통 설정. 지금은 설비 한 대(LW-01)를 본다.</summary>
public sealed class SiteContext(IConfiguration configuration)
{
    public string EquipmentId { get; } = configuration["WeldTrace:EquipmentId"] ?? "LW-01";
}

/// <summary>
/// 시작할 때 DB가 준비될 때까지 기다렸다가 마이그레이션을 적용한다 (수집기와 동시에 떠도 잠금으로 안전).
/// </summary>
public sealed class DatabaseStartup(IConfiguration configuration, ILogger<DatabaseStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("WeldTrace")
                               ?? throw new InvalidOperationException("ConnectionStrings:WeldTrace 설정이 없습니다.");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await SqlMigrator.EnsureDatabaseAsync(connectionString, cancellationToken);
                await SqlMigrator.MigrateAsync(connectionString, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < 30 && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("DB 준비 대기 중 ({Attempt}/30): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>열화 감시: 일별 평균 출력 비율 → DriftAnalyzer (가동일 단위 관리도).</summary>
public static class DriftView
{
    public const int BaselineDays = 10;
    public const int MinimumDays = BaselineDays * 2;

    public static async Task<DriftReport?> AnalyzeAsync(QueryService q, string equipmentId, CancellationToken ct)
    {
        var daily = await q.DailyQualityAsync(equipmentId, ct);
        var series = daily.Where(d => d.MeanPowerRatio is not null)
            .Select(d => (d.WorkDate, d.MeanPowerRatio!.Value))
            .ToList();
        return DriftAnalyzer.Analyze(series, baselineCount: BaselineDays);
    }
}

/// <summary>화면 표시 형식.</summary>
public static class Fmt
{
    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

    public static string Pct(double value, int digits = 1) => (value * 100).ToString("F" + digits, Ko) + "%";
    public static string Ratio(double? value) => value is { } v ? v.ToString("F4", CultureInfo.InvariantCulture) : "—";
    public static string Num(long value) => value.ToString("N0", Ko);
    public static string Date(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—";
    public static string Time(DateTime? value) => value?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "—";

    private static readonly TimeZoneInfo Plant = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");

    /// <summary>
    /// DB가 UTC로 기록한 시각(배치 시작·완료)을 공장 현지 시각(KST)으로 표시한다.
    /// 용접 시각은 설비가 현지 시각으로 찍으므로 변환하지 않는다.
    /// </summary>
    public static string UtcTime(DateTime? utc) =>
        utc is { } v ? Time(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(v, DateTimeKind.Utc), Plant)) : "—";

    public static string VerdictLabel(string verdict) => verdict switch
    {
        "OK" => "정상",
        "WARNING" => "경고",
        "NG" => "불량",
        "HOLD" => "보류",
        _ => verdict,
    };

    public static string FaultLabel(string? fault) => fault switch
    {
        "NO_OUTPUT" => "미출력",
        "POWER_DROP" => "출력 저하",
        "RECIPE_DEVIATION" => "설정 이탈",
        null or "" => "",
        _ => fault,
    };

    public static string VerdictClass(string verdict) => verdict switch
    {
        "OK" => "ok",
        "WARNING" => "warn",
        "NG" => "ng",
        _ => "hold",
    };
}
