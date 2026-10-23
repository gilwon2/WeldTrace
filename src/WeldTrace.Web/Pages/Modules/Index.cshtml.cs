using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WeldTrace.Data;

namespace WeldTrace.Web.Pages.Modules;

public sealed class IndexModel(QueryService query, SiteContext site) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public DateTime? Date { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Verdict { get; set; }

    public IReadOnlyList<DateTime> Dates { get; private set; } = [];
    public IReadOnlyList<ModuleSummary> Modules { get; private set; } = [];

    public static readonly string[] Verdicts = ["OK", "WARNING", "NG", "HOLD"];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Dates = await query.WorkDatesAsync(site.EquipmentId, ct);
        if (Verdict is not null && !Verdicts.Contains(Verdict))
            Verdict = null;
        // 날짜를 고르지 않았으면 가장 최근 가동일을 보여 준다 (불량만 볼 때는 전체 기간)
        if (Date is null && Verdict is null && Dates.Count > 0)
            Date = Dates[0];
        Modules = await query.ModulesAsync(site.EquipmentId, Date, Verdict, take: 500, ct: ct);
    }
}
