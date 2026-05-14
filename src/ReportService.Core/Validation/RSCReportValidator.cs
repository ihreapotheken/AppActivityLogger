using ReportService.Models;
using ReportService.Options;

namespace ReportService.Validation;

/// <summary>
/// Validates the metadata of inbound <see cref="RSCProblemReport"/> payloads and the companion gzip
/// attachment against configured limits and the multipart contract shared with the IA SDKs.
/// </summary>
public sealed class RSCReportValidator
{
    private const int MaxMessageChars = 8192;
    private const int MaxTitleChars = 256;
    private const int MaxEmailChars = 512;
    private const int MaxPhoneChars = 64;
    private const int MaxPharmacyIdChars = 128;
    private const int MaxUserIdChars = 256;
    private const int MaxSourceChars = 32;
    private const int MaxAppVersionChars = 256;
    private const int MaxFunctionalityImportanceChars = 256;
    private const int MaxLabels = 32;
    private const int MaxLabelChars = 128;

    private static readonly byte[] GzipMagic = { 0x1F, 0x8B };

    private readonly RSCReportServiceOptions _options;

    public RSCReportValidator(RSCReportServiceOptions options) => _options = options;

    /// <summary>
    /// Validates a deserialized problem report: required fields, length caps on every optional
    /// string, and the platform allow-list. Returns the first failure encountered.
    /// </summary>
    public RSCValidationResult ValidateReport(RSCProblemReport? report)
    {
        if (report is null) return RSCValidationResult.Fail("request body is empty");

        if (string.IsNullOrWhiteSpace(report.Platform))
            return RSCValidationResult.Fail("platform is required");
        if (RSCPlatforms.TryCanonicalize(report.Platform, _options) is null)
            return RSCValidationResult.Fail($"platform '{report.Platform}' is not allowed");

        if (string.IsNullOrWhiteSpace(report.Message))
            return RSCValidationResult.Fail("message is required");
        if (TooLong(report.Message, MaxMessageChars, "message") is { } e2) return e2;

        if (TooLong(report.Title, MaxTitleChars, "title") is { } e3) return e3;
        if (TooLong(report.Email, MaxEmailChars, "email") is { } e4) return e4;
        if (TooLong(report.PhoneNumber, MaxPhoneChars, "phoneNumber") is { } e5) return e5;
        if (TooLong(report.Phone, MaxPhoneChars, "phone") is { } e6) return e6;
        if (TooLong(report.PharmacyId, MaxPharmacyIdChars, "pharmacyId") is { } e7) return e7;
        if (TooLong(report.UserId, MaxUserIdChars, "userId") is { } e7b) return e7b;
        if (TooLong(report.Source, MaxSourceChars, "source") is { } e8) return e8;
        if (TooLong(report.AppVersion, MaxAppVersionChars, "appVersion") is { } e9) return e9;
        if (TooLong(report.FunctionalityImportance, MaxFunctionalityImportanceChars, "functionalityImportance") is { } e10) return e10;

        if (report.Labels is { } labels)
        {
            if (labels.Count > MaxLabels)
                return RSCValidationResult.Fail("labels exceeds max count");
            for (var i = 0; i < labels.Count; i++)
            {
                var label = labels[i];
                if (label is null)
                    return RSCValidationResult.Fail($"labels[{i}] is null");
                if (label.Length > MaxLabelChars)
                    return RSCValidationResult.Fail($"labels[{i}] exceeds max length");
            }
        }

        return RSCValidationResult.Ok();
    }

    /// <summary>
    /// Validates the companion gzip attachment: size cap + gzip magic header (<c>1F 8B</c>).
    /// </summary>
    public RSCValidationResult ValidateAttachment(long attachmentLength, long maxAttachmentBytes, byte[] firstTwoBytes)
    {
        if (attachmentLength > maxAttachmentBytes)
            return RSCValidationResult.Fail("attachment exceeds MaxAttachmentBytes");

        if (firstTwoBytes is null || firstTwoBytes.Length < 2
            || firstTwoBytes[0] != GzipMagic[0] || firstTwoBytes[1] != GzipMagic[1])
        {
            return RSCValidationResult.Fail("attachment is not a gzip stream");
        }

        return RSCValidationResult.Ok();
    }

    private static RSCValidationResult? TooLong(string? value, int max, string field)
        => value is { Length: var len } && len > max
            ? RSCValidationResult.Fail($"{field} exceeds max length")
            : null;
}
