using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReportService.Observability;

/// <summary>Process-wide unhandled-exception hooks that log the fault and ask the host to stop cleanly before the CLR tears the process down.</summary>
public sealed class RSCCrashHandler
{
    private static int _installed;

    private RSCCrashHandler() { }

    /// <summary>Installs AppDomain + TaskScheduler hooks. Idempotent per process.</summary>
    public static void Install(ILogger logger, IHostApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(lifetime);

        if (Interlocked.Exchange(ref _installed, 1) == 1)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            logger.LogCritical(
                ex,
                "Unhandled AppDomain exception (isTerminating={IsTerminating}): {ExceptionObject}",
                args.IsTerminating,
                args.ExceptionObject);

            // Ask the host to stop so ILoggerProviders and hosted services flush cleanly.
            // Do NOT swallow: once StopApplication returns, we let the process terminate as the CLR intended.
            try
            {
                lifetime.StopApplication();
            }
            catch
            {
                // Nothing we can do here; the CLR is already tearing down.
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            // Prevent the finalizer thread from escalating this to a process-wide crash on older runtimes.
            e.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            logger.LogInformation("Process exiting");
        };
    }
}
