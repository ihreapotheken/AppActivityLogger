using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Storage;

namespace ReportService.Admin.Pages;

/// <summary>
/// Maintains the forced-report allow-list. IDs added here cause the public
/// <c>GET /api/v1/forced-reports/{id}</c> endpoint to return <c>forced=true</c>, which the mobile
/// app uses to instruct itself to forcefully submit a Report-a-Problem entry on the next backend
/// fetch. The list is a thin admin-managed allow-list — no data dependency on report submissions.
/// </summary>
public sealed class RSAForcedReportsModel : PageModel
{
    private readonly RSCIForcedReportStore _store;

    public RSAForcedReportsModel(RSCIForcedReportStore store)
    {
        _store = store;
    }

    public IReadOnlyList<RSCForcedReportEntry> Entries { get; private set; } = Array.Empty<RSCForcedReportEntry>();
    public string? Flash { get; private set; }
    public string? FlashKind { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Entries = await _store.ListAsync(ct);

        // The Add/Remove handlers redirect back here with ?flash=... so a successful action
        // surfaces a confirmation toast on the very next render. Keeps the page model
        // statelessly bindable.
        if (Request.Query.TryGetValue("flash", out var flash))
        {
            Flash = flash.ToString();
            FlashKind = Request.Query.TryGetValue("kind", out var kind) ? kind.ToString() : "ok";
        }
    }

    public async Task<IActionResult> OnPostAddAsync([FromForm] string id, [FromForm] string? note, CancellationToken ct)
    {
        var trimmed = (id ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 256)
        {
            return RedirectToPage(new { flash = "ID must be 1..256 characters", kind = "err" });
        }

        var inserted = await _store.AddAsync(trimmed, string.IsNullOrWhiteSpace(note) ? null : note!.Trim(), ct);
        var verb = inserted ? "added" : "updated";
        return RedirectToPage(new { flash = $"{verb}: {trimmed}", kind = "ok" });
    }

    public async Task<IActionResult> OnPostRemoveAsync([FromForm] string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return RedirectToPage();
        }
        var removed = await _store.RemoveAsync(id, ct);
        return RedirectToPage(new
        {
            flash = removed ? $"removed: {id}" : $"not found: {id}",
            kind = removed ? "ok" : "err"
        });
    }
}
