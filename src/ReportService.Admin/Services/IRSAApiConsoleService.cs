namespace ReportService.Admin.Services;

/// <summary>
/// Supplies the bundled API request library (a Postman v2.1 collection + its local environment) to the
/// in-dashboard API console (<c>/ApiConsole</c>). The server only loads + hands over the raw JSON; all
/// of the Postman semantics — folder flattening, <c>{{variable}}</c> substitution, auth resolution and
/// the (same-origin) request execution — happen in the browser (see <c>wwwroot/js/api-console.js</c>).
/// </summary>
public interface IRSAApiConsoleService
{
    /// <summary>The loaded collection + environment, or <c>null</c> when no collection ships in this build.</summary>
    RSAApiConsoleFixtures? Load();
}

/// <summary>
/// Raw fixture payloads for the API console. <see cref="CollectionJson"/> is a Postman v2.1 collection;
/// <see cref="EnvironmentJson"/> is an optional Postman environment (may be <c>null</c> when none ships).
/// Both are passed verbatim to the page as JSON data blocks.
/// </summary>
public sealed record RSAApiConsoleFixtures(string CollectionJson, string? EnvironmentJson);
