using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Audit;

namespace ReportService.Admin.Pages;

/// <summary>Recent audit entries for the operator console. Read-only.</summary>
public sealed class RSAAuditModel : PageModel
{
    private readonly RSCIAuditLog _audit;

    public RSAAuditModel(RSCIAuditLog audit) => _audit = audit;

    public IReadOnlyList<RSCAuditEntry> Entries { get; private set; } = Array.Empty<RSCAuditEntry>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Entries = await _audit.RecentAsync(200, ct).ConfigureAwait(false);
    }
}
