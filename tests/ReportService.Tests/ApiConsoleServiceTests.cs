using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Admin.Options;
using ReportService.Admin.Services;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Unit coverage for <see cref="RSAApiConsoleService"/> — the loader behind the /ApiConsole page.
/// Points <see cref="RSAAdminOptions.ApiFixturesRoot"/> at an isolated temp dir so the test doesn't
/// depend on the csproj bundle landing in the test host's output.
/// </summary>
public class ApiConsoleServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rs-apic-{Guid.NewGuid():N}");

    public ApiConsoleServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private RSAApiConsoleService NewService() =>
        new(new RSAAdminOptions { ApiFixturesRoot = _root }, NullLogger<RSAApiConsoleService>.Instance);

    [Fact]
    public void Returns_null_when_no_collection_ships()
    {
        Assert.Null(NewService().Load());
    }

    [Fact]
    public void Loads_collection_and_optional_environment()
    {
        File.WriteAllText(Path.Combine(_root, "collection.json"), """{ "info": { "name": "demo" }, "item": [] }""");
        File.WriteAllText(Path.Combine(_root, "environment.json"), """{ "values": [{ "key": "baseUrl", "value": "x" }] }""");

        var loaded = NewService().Load();

        Assert.NotNull(loaded);
        Assert.Contains("\"name\": \"demo\"", loaded!.CollectionJson);
        Assert.NotNull(loaded.EnvironmentJson);
        Assert.Contains("baseUrl", loaded.EnvironmentJson!);
    }

    [Fact]
    public void Collection_without_environment_still_loads()
    {
        File.WriteAllText(Path.Combine(_root, "collection.json"), """{ "item": [] }""");

        var loaded = NewService().Load();

        Assert.NotNull(loaded);
        Assert.Null(loaded!.EnvironmentJson);
    }

    [Fact]
    public void Malformed_collection_is_treated_as_absent()
    {
        File.WriteAllText(Path.Combine(_root, "collection.json"), "{ this is not json");

        Assert.Null(NewService().Load());
    }

    [Fact]
    public void Result_is_cached_after_first_load()
    {
        File.WriteAllText(Path.Combine(_root, "collection.json"), """{ "item": [] }""");
        var svc = NewService();

        var first = svc.Load();
        // Mutating the file after the first load must not change what a cached singleton returns.
        File.WriteAllText(Path.Combine(_root, "collection.json"), """{ "item": [1,2,3] }""");
        var second = svc.Load();

        Assert.Same(first, second);
    }
}
