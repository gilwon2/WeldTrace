using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WeldTrace.Data;

namespace WeldTrace.Web.Pages.Modules;

public sealed class DetailModel(QueryService query) : PageModel
{
    public const double Threshold = 0.95;

    public ModuleTrace Trace { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        var trace = await query.ModuleTraceAsync(id, ct);
        if (trace is null)
            return NotFound();
        Trace = trace;
        return Page();
    }

    public object ChartData => new
    {
        labels = Trace.Steps.Select(s => s.PageNo.ToString()),
        ratio = Trace.Steps.Select(s => s.PowerRatio is { } r ? Math.Round(r, 4) : (double?)null),
        faulty = Trace.Steps.Select(s => s.Faults is not null && s.Faults.Split(',').Any(f => f != "RECIPE_DEVIATION")),
        deviation = Trace.Steps.Select(s => s.SetPower != s.ExpectedSetPower),
        threshold = Threshold,
    };
}
