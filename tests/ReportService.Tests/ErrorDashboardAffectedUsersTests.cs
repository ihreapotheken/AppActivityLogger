using System.Text;
using ReportService.Admin.Services;
using ReportService.Admin.ViewModels;
using ReportService.Models;
using ReportService.Options;
using ReportService.Storage;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Pins the "affected users" count for the error dashboard. Earlier the key was
/// <c>DeviceModel|PharmacyId</c>, which collapsed every install of the same phone in the same
/// pharmacy into a single user — operators got 1 affected user when 50 distinct people had
/// actually crashed. The dashboard now keys on the SDK-supplied per-install <c>UserId</c> and
/// only falls back to a per-report key when that field is missing.
/// </summary>
public class ErrorDashboardAffectedUsersTests
{
    [Fact]
    public void Counts_one_affected_user_per_distinct_userId()
    {
        var store = new InMemoryStore();
        var now = DateTimeOffset.UtcNow;
        store.Add("android", "a.json", CrashJson(now, userId: "user-1", deviceModel: "Pixel 6", pharmacyId: "10001"));
        store.Add("android", "b.json", CrashJson(now, userId: "user-2", deviceModel: "Pixel 6", pharmacyId: "10001"));
        store.Add("android", "c.json", CrashJson(now, userId: "user-3", deviceModel: "Pixel 6", pharmacyId: "10001"));

        var vm = BuildDashboard(store);

        Assert.Equal(3, vm.AffectedUsers);
        Assert.Equal(3, vm.Platforms.Single(p => p.Name == "android").AffectedUsers);
    }

    [Fact]
    public void Collapses_repeat_crashes_from_same_userId()
    {
        var store = new InMemoryStore();
        var now = DateTimeOffset.UtcNow;
        store.Add("android", "a.json", CrashJson(now, userId: "user-1", deviceModel: "Pixel 6", pharmacyId: "10001"));
        store.Add("android", "b.json", CrashJson(now, userId: "user-1", deviceModel: "Pixel 6", pharmacyId: "10001"));
        store.Add("android", "c.json", CrashJson(now, userId: "user-1", deviceModel: "Pixel 6", pharmacyId: "10001"));

        var vm = BuildDashboard(store);

        Assert.Equal(1, vm.AffectedUsers);
    }

    [Fact]
    public void Legacy_reports_without_userId_each_count_as_one_user()
    {
        // The dashboard used to collapse every UserId-less report to a single (DeviceModel|PharmacyId)
        // bucket — three crashes from three different people on the same phone model showed up as
        // one affected user. With no UserId to disambiguate them, the only honest count is one user
        // per report.
        var store = new InMemoryStore();
        var now = DateTimeOffset.UtcNow;
        store.Add("android", "a.json", CrashJson(now, userId: null, deviceModel: "Pixel 6", pharmacyId: "10001"));
        store.Add("android", "b.json", CrashJson(now, userId: null, deviceModel: "Pixel 6", pharmacyId: "10001"));
        store.Add("android", "c.json", CrashJson(now, userId: null, deviceModel: "Pixel 6", pharmacyId: "10001"));

        var vm = BuildDashboard(store);

        Assert.Equal(3, vm.AffectedUsers);
    }

    [Fact]
    public void Mixed_userId_and_legacy_combine_correctly()
    {
        var store = new InMemoryStore();
        var now = DateTimeOffset.UtcNow;
        store.Add("android", "a.json", CrashJson(now, userId: "user-1", deviceModel: "Pixel 6", pharmacyId: "10001"));
        store.Add("android", "b.json", CrashJson(now, userId: "user-1", deviceModel: "Pixel 6", pharmacyId: "10001"));
        store.Add("android", "c.json", CrashJson(now, userId: null, deviceModel: "Pixel 6", pharmacyId: "10001"));
        store.Add("android", "d.json", CrashJson(now, userId: null, deviceModel: "Pixel 6", pharmacyId: "10001"));

        var vm = BuildDashboard(store);

        // 1 distinct UserId + 2 legacy reports (each counted once) = 3.
        Assert.Equal(3, vm.AffectedUsers);
    }

    [Fact]
    public void Top_error_signature_carries_affected_user_count()
    {
        var store = new InMemoryStore();
        var now = DateTimeOffset.UtcNow;
        const string frame = "de.ihreapotheken.app.Foo.bar(Foo.kt:42)";
        store.Add("android", "a.json", CrashJson(now, userId: "user-1", topFrame: frame), topFrame: frame);
        store.Add("android", "b.json", CrashJson(now, userId: "user-2", topFrame: frame), topFrame: frame);
        store.Add("android", "c.json", CrashJson(now, userId: "user-2", topFrame: frame), topFrame: frame);

        var vm = BuildDashboard(store);

        var top = Assert.Single(vm.TopErrors);
        Assert.Equal(3, top.Occurrences);
        Assert.Equal(2, top.AffectedUsers);
    }

    private static RSAErrorDashboardVM BuildDashboard(InMemoryStore store)
    {
        var options = new RSCReportServiceOptions { AllowedPlatforms = new[] { "android", "ios" } };
        var svc = new RSAReportStoreErrorDashboardService(store, options);
        return svc.Build();
    }

    private static string CrashJson(DateTimeOffset occurredAt, string? userId = null,
                                    string? deviceModel = null, string? pharmacyId = null,
                                    string? topFrame = null)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"kind\":\"crash\",");
        sb.Append("\"platform\":\"android\",");
        sb.Append($"\"occurredAt\":\"{occurredAt:O}\",");
        sb.Append("\"message\":\"boom\",");
        if (userId is not null) sb.Append($"\"userId\":\"{userId}\",");
        if (deviceModel is not null) sb.Append($"\"deviceModel\":\"{deviceModel}\",");
        if (pharmacyId is not null) sb.Append($"\"pharmacyId\":\"{pharmacyId}\",");
        if (topFrame is not null) sb.Append($"\"stackTrace\":\"java.lang.RuntimeException: boom\\n\\tat {topFrame}\",");
        sb.Length--; // trim trailing comma
        sb.Append('}');
        return sb.ToString();
    }

    private sealed class InMemoryStore : RSCIReportStore
    {
        private readonly Dictionary<string, List<(RSCStoredReport meta, byte[] body)>> _byPlatform = new();

        public void Add(string platform, string fileName, string body, string? topFrame = null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var meta = new RSCStoredReport(
                Platform: platform,
                FileName: fileName,
                SizeBytes: bytes.Length,
                SubmittedAt: DateTimeOffset.UtcNow,
                AttachmentFileName: null,
                AttachmentSizeBytes: null,
                IngestionChannel: "multipart",
                TopFrame: topFrame,
                LogSummaryJson: null,
                Kind: "crash");
            if (!_byPlatform.TryGetValue(platform, out var list))
            {
                list = new List<(RSCStoredReport, byte[])>();
                _byPlatform[platform] = list;
            }
            list.Add((meta, bytes));
        }

        public Task<RSCStoredReport> SaveAsync(RSCProblemReport report, ReadOnlyMemory<byte> jsonBytes,
                                               Stream? attachment, long? attachmentLength,
                                               string ingestionChannel, CancellationToken ct)
            => throw new NotSupportedException();

        public IReadOnlyList<RSCStoredReport> List(string platform) =>
            _byPlatform.TryGetValue(platform, out var list)
                ? list.Select(x => x.meta).ToArray()
                : Array.Empty<RSCStoredReport>();

        public Stream? OpenRead(string platform, string fileName) =>
            _byPlatform.TryGetValue(platform, out var list) && list.FirstOrDefault(x => x.meta.FileName == fileName) is { body: { } body }
                ? new MemoryStream(body)
                : null;

        public bool Delete(string platform, string fileName) => false;
    }
}
