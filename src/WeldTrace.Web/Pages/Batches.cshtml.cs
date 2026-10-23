using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WeldTrace.Data;

namespace WeldTrace.Web.Pages;

public sealed class BatchesModel(QueryService query) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? Id { get; set; }

    public IReadOnlyList<BatchSummary> Batches { get; private set; } = [];
    public IReadOnlyList<IssueRow> Issues { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Batches = await query.RecentBatchesAsync(100, ct);
        if (Id is { } id)
            Issues = await query.BatchIssuesAsync(id, ct);
    }
}
