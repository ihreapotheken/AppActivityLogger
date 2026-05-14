using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Models;
using ReportService.Options;
using ReportService.Storage;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Regression coverage for the "same JSON + different attachment in the same second overwrites
/// each other" bug. The fix mixes a hash of the attachment bytes into the filename, so two uploads
/// that share JSON but carry different attachments must land on distinct paths.
/// </summary>
public class StorageCollisionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rs-storage-{Guid.NewGuid():N}");
    private readonly RSCFileSystemReportStore _store;

    public StorageCollisionTests()
    {
        var options = new RSCReportServiceOptions
        {
            ReportsRoot = _root,
            AllowedPlatforms = new[] { "android", "ios" }
        };
        _store = new RSCFileSystemReportStore(options, NullLogger<RSCFileSystemReportStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Identical_json_without_attachment_is_idempotent()
    {
        var report = new RSCProblemReport(
            Platform: "Android", Message: "Test",
            Title: null, DeviceModel: null, Email: null, PhoneNumber: null, Phone: null,
            PharmacyId: null, Source: null, AppVersion: null, FunctionalityImportance: null, Labels: null);
        var json = System.Text.Encoding.UTF8.GetBytes("{\"same\":\"content\"}");

        var a = await _store.SaveAsync(report, json, null, null, RSCIngestionChannels.Multipart, default);
        var b = await _store.SaveAsync(report, json, null, null, RSCIngestionChannels.Multipart, default);

        Assert.Equal(a.FileName, b.FileName);
    }

    [Fact]
    public async Task Same_json_but_different_attachments_yield_distinct_filenames()
    {
        var report = new RSCProblemReport(
            Platform: "Android", Message: "Test",
            Title: null, DeviceModel: null, Email: null, PhoneNumber: null, Phone: null,
            PharmacyId: null, Source: null, AppVersion: null, FunctionalityImportance: null, Labels: null);
        var json = System.Text.Encoding.UTF8.GetBytes("{\"same\":\"content\"}");

        var attachmentA = Bytes(payload: 0xAA);
        var attachmentB = Bytes(payload: 0xBB);

        using var streamA = new MemoryStream(attachmentA);
        using var streamB = new MemoryStream(attachmentB);

        var a = await _store.SaveAsync(report, json, streamA, attachmentA.Length, RSCIngestionChannels.Multipart, default);
        var b = await _store.SaveAsync(report, json, streamB, attachmentB.Length, RSCIngestionChannels.Multipart, default);

        Assert.NotEqual(a.FileName, b.FileName);
        Assert.NotEqual(a.AttachmentFileName, b.AttachmentFileName);
        Assert.NotNull(a.AttachmentFileName);
        Assert.NotNull(b.AttachmentFileName);

        // Both sets of bytes must survive on disk.
        using var readA = _store.OpenRead("android", a.AttachmentFileName!)!;
        using var readB = _store.OpenRead("android", b.AttachmentFileName!)!;
        var loadedA = new byte[attachmentA.Length];
        var loadedB = new byte[attachmentB.Length];
        ReadExactly(readA, loadedA);
        ReadExactly(readB, loadedB);
        Assert.Equal(attachmentA, loadedA);
        Assert.Equal(attachmentB, loadedB);
    }

    [Fact]
    public async Task Same_json_and_same_attachment_are_idempotent()
    {
        var report = new RSCProblemReport(
            Platform: "Android", Message: "Test",
            Title: null, DeviceModel: null, Email: null, PhoneNumber: null, Phone: null,
            PharmacyId: null, Source: null, AppVersion: null, FunctionalityImportance: null, Labels: null);
        var json = System.Text.Encoding.UTF8.GetBytes("{\"same\":\"content\"}");
        var attachment = Bytes(payload: 0xCC);

        using var s1 = new MemoryStream(attachment);
        using var s2 = new MemoryStream(attachment);

        var a = await _store.SaveAsync(report, json, s1, attachment.Length, RSCIngestionChannels.Multipart, default);
        var b = await _store.SaveAsync(report, json, s2, attachment.Length, RSCIngestionChannels.Multipart, default);

        Assert.Equal(a.FileName, b.FileName);
        Assert.Equal(a.AttachmentFileName, b.AttachmentFileName);
    }

    private static byte[] Bytes(byte payload)
    {
        var data = new byte[256];
        Array.Fill(data, payload);
        return data;
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = stream.Read(buffer, offset, buffer.Length - offset);
            if (n <= 0) break;
            offset += n;
        }
    }
}
