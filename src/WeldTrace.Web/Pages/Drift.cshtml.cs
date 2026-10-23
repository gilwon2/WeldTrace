using Microsoft.AspNetCore.Mvc.RazorPages;
using WeldTrace.Core.Spc;
using WeldTrace.Data;

namespace WeldTrace.Web.Pages;

public sealed class DriftModel(QueryService query, SiteContext site) : PageModel
{
    public DriftReport? Report { get; private set; }
    public int Days { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var daily = await query.DailyQualityAsync(site.EquipmentId, ct);
        Days = daily.Count(d => d.MeanPowerRatio is not null);
        Report = await DriftView.AnalyzeAsync(query, site.EquipmentId, ct);
    }

    public object ChartData
    {
        get
        {
            var r = Report!;
            return new
            {
                labels = r.Points.Select(p => Fmt.Date(p.Time)),
                value = r.Points.Select(p => Math.Round(p.Value, 6)),
                ewma = r.Points.Select(p => Math.Round(p.Ewma, 6)),
                lcl = r.Points.Select(p => Math.Round(p.Lcl, 6)),
                ucl = r.Points.Select(p => Math.Round(p.Ucl, 6)),
                alarm = r.Points.Select(p => p.EwmaAlarm),
                cusum = r.Points.Select(p => Math.Round(p.CusumLower, 6)),
                h = Math.Round(5 * r.Baseline.StdDev, 6),
                baselineDays = DriftView.BaselineDays,
                trend = r.Points.Select(p => Math.Round(r.Trend.ValueAt((p.Time - r.Points[0].Time).TotalDays), 6)),
            };
        }
    }
}
