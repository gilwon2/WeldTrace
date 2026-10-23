using Microsoft.AspNetCore.Mvc.RazorPages;
using WeldTrace.Core.Spc;
using WeldTrace.Data;

namespace WeldTrace.Web.Pages;

public sealed class IndexModel(QueryService query, SiteContext site) : PageModel
{
    public Overview Overview { get; private set; } = new();
    public IReadOnlyList<DailyQuality> Daily { get; private set; } = [];
    public IReadOnlyList<ModuleSummary> RecentNg { get; private set; } = [];
    public DriftReport? Drift { get; private set; }

    public bool HasData => Overview.Modules > 0;
    public DailyQuality? LastDay => Daily.Count > 0 ? Daily[^1] : null;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Overview = await query.OverviewAsync(site.EquipmentId, ct);
        if (!HasData)
            return;
        Daily = await query.DailyQualityAsync(site.EquipmentId, ct);
        RecentNg = await query.ModulesAsync(site.EquipmentId, verdict: "NG", take: 8, ct: ct);
        Drift = await DriftView.AnalyzeAsync(query, site.EquipmentId, ct);
    }

    /// <summary>차트용: 날짜, 불량률(%), 일평균 출력 비율.</summary>
    public object ChartData => new
    {
        labels = Daily.Select(d => Fmt.Date(d.WorkDate)),
        ngRate = Daily.Select(d => Math.Round(d.NgRate * 100, 2)),
        modules = Daily.Select(d => d.Modules),
        ratio = Daily.Select(d => d.MeanPowerRatio is { } r ? Math.Round(r, 5) : (double?)null),
    };
}
