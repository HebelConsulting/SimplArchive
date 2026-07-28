using Serilog;
using Serilog.Formatting.Compact;

namespace SimplArchive.Api.Logging;

/// <summary>
/// Configures the application's Serilog logger (ADR "Enterprise-grade structured logging with Serilog").
/// Levels are Serilog's six — Verbose / Debug / Information / Warning / Error / Fatal (see the logging
/// principle in CLAUDE.md). Sinks: a human-readable console in Development, compact structured JSON to stdout
/// everywhere else (12-factor — the container / Kubernetes / SIEM collects stdout). Minimum levels + per-source
/// overrides are read from the <c>Serilog</c> configuration section so they're tunable without a redeploy.
/// </summary>
public static class SerilogConfiguration
{
    // Development console: timestamp, 3-letter level, message, exception — deliberately terse (no property bag)
    // so a dev's console stays readable. Structured properties still ride on every event and appear in the JSON
    // sink used outside Development.
    private const string DevelopmentTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    public static void Configure(LoggerConfiguration logger, IConfiguration configuration, IHostEnvironment environment, IServiceProvider services)
    {
        logger
            .ReadFrom.Configuration(configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "SimplArchive.Api")
            .Enrich.WithProperty("Environment", environment.EnvironmentName);

        if (environment.IsDevelopment())
        {
            logger.WriteTo.Console(outputTemplate: DevelopmentTemplate);
        }
        else
        {
            logger.WriteTo.Console(new CompactJsonFormatter());
        }
    }

    /// <summary>The bootstrap logger used before the host (and its configuration/services) is available.</summary>
    public static Serilog.ILogger CreateBootstrapLogger() =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: DevelopmentTemplate)
            .CreateBootstrapLogger();
}
