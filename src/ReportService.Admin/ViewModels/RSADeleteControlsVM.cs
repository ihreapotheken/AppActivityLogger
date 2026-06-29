namespace ReportService.Admin.ViewModels;

/// <summary>Drives the "delete all matching" bar on a listing page. <see cref="PostUrl"/> carries the
/// active filter query plus <c>&amp;handler=DeleteMatching</c>; <see cref="MatchCount"/> is the exact
/// number that will be deleted (shown in the button + confirm prompt).</summary>
public sealed record RSADeleteBarVM(string PostUrl, int MatchCount, string Noun);

/// <summary>Drives a single row's delete form. <see cref="PostUrl"/> carries the active filter query
/// plus <c>&amp;handler=DeleteOne</c> so the page returns to the same filtered view after deleting.</summary>
public sealed record RSARowDeleteVM(string PostUrl, string Platform, string FileName);
