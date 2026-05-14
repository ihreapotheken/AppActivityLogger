using System.Collections.Generic;

namespace ReportService.Mappings;

/// <summary>
/// Applies an ordered chain of <see cref="RSCMappingFile"/> entries to a stack-trace string.
/// Each link in the chain runs the trace through its own <see cref="RSCMappingApplier"/> in
/// sequence, mirroring R8's <c>retrace --mapping-file ... --mapping-file ...</c> semantics:
/// the first link reverses the host's renaming, the next reverses an SDK consumer mapping, etc.
/// Frame counts are summed across links so the UI can show "rewrote N frames across M
/// mappings".
/// </summary>
public sealed class RSCMappingChainApplier
{
    private readonly IReadOnlyList<RSCMappingFile> _chain;

    public RSCMappingChainApplier(IReadOnlyList<RSCMappingFile> chain)
    {
        _chain = chain;
    }

    public int RewrittenFrames { get; private set; }

    public string Apply(string trace)
    {
        if (_chain.Count == 0 || string.IsNullOrEmpty(trace)) return trace;
        var current = trace;
        var total = 0;
        foreach (var link in _chain)
        {
            var applier = new RSCMappingApplier(link);
            current = applier.Apply(current);
            total += applier.RewrittenFrames;
        }
        RewrittenFrames = total;
        return current;
    }
}
