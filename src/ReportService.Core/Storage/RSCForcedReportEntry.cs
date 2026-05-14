namespace ReportService.Storage;

/// <summary>
/// One row of the forced-report allow-list. <see cref="Id"/> is the operator-supplied identifier
/// the mobile client checks against; <see cref="Note"/> is free-form context shown in the admin
/// UI (e.g. "investigating ticket #4214") so an operator returning to the list a week later can
/// remember why a given ID is on it.
/// </summary>
public sealed record RSCForcedReportEntry(
    string Id,
    DateTimeOffset AddedAt,
    string? Note);
