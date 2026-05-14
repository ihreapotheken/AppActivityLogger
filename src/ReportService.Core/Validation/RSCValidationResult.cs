namespace ReportService.Validation;

/// <summary>Outcome of a validation check: either <c>IsValid</c> with no error, or failed with the first failure reason in <c>Error</c>.</summary>
public sealed record RSCValidationResult(bool IsValid, string? Error)
{
    public static RSCValidationResult Ok() => new(true, null);
    public static RSCValidationResult Fail(string reason) => new(false, reason);
}
