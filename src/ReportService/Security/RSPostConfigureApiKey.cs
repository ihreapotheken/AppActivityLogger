using Microsoft.Extensions.Options;
using ReportService.Options;

namespace ReportService.Security;

// Pushes RSCReportServiceOptions.ApiKey into the auth scheme at resolve time, so test hosts that
// swap the RSCReportServiceOptions singleton via ConfigureTestServices see their value flow through.
public sealed class RSPostConfigureApiKey : IPostConfigureOptions<RSApiKeyAuthenticationOptions>
{
    private readonly RSCReportServiceOptions _options;

    public RSPostConfigureApiKey(RSCReportServiceOptions options) => _options = options;

    public void PostConfigure(string? name, RSApiKeyAuthenticationOptions options)
        => options.ExpectedKey = _options.ApiKey;
}
