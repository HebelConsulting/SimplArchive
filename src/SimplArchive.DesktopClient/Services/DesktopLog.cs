using System;
using System.IO;
using System.Linq;
using Serilog;
using Serilog.Events;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The desktop client's log (ADR 0613). One rolling text file beside the client's other state, plus the console
/// while a developer is watching.
/// </summary>
/// <remarks>
/// <para>
/// Why it exists: the server has had structured logging since ADR 0430, while the half of the product that runs
/// on the user's machine — the half that touches their filesystem, network, scanner and mount — recorded
/// nothing. The trigger was a style that no longer resolves: it falls back silently, which is right (a colour
/// scheme is never worth interrupting somebody's work), but "no dialog" should not mean "no trace", and the
/// existing `Console.Error.WriteLine` goes **nowhere** in a packaged build — no terminal is attached to an
/// `.app` bundle, a `.zip` or a tarball.
/// </para>
/// <para>
/// TEXT rather than JSON, unlike the migration tooling (ADR 0586): this file's first reader is a support
/// conversation. Somebody has to find it, glance at it and paste the last lines into an email — and lines paste
/// better than JSON objects. Properties still ride on every event; only the rendering is prose.
/// </para>
/// <para>
/// NEVER log a secret. The client holds access tokens and, for a mount, a password. The rule is the server's
/// (ADR 0430) and is easier to break here, because a log call in a view-model gets less scrutiny than one in a
/// request pipeline. Log what happened and to which object, never the credential that authorised it.
/// </para>
/// </remarks>
public static class DesktopLog
{
    private const string Template = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    // Small on purpose: this is a support artefact, not telemetry. Seven files of 4 MB is more history than any
    // support conversation has ever needed, and it costs a user nothing they would notice.
    private const long FileSizeLimitBytes = 4L * 1024 * 1024;
    private const int RetainedFiles = 7;

    private static ILogger _logger = Serilog.Core.Logger.None;

    /// <summary>The folder the log files live in — what Help ▸ Show log folder opens.</summary>
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimplArchive", "logs");

    /// <summary>Called once at startup, before anything that might want to log.</summary>
    /// <param name="verbose">
    /// <c>--verbose</c>: lift the CONSOLE to Debug. The file has always carried Debug (the pipeline minimum);
    /// this flag only changes what a person watching a terminal sees, so "run it with --verbose and read the
    /// console" and "send me the log file" describe the same detail through two channels.
    /// </param>
    public static void Initialize(bool verbose = false)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            _logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.WithProperty("Application", "SimplArchive.DesktopClient")
                .Enrich.WithProperty("Version", typeof(DesktopLog).Assembly.GetName().Version?.ToString() ?? "unknown")
                .WriteTo.Console(outputTemplate: Template,
                    restrictedToMinimumLevel: verbose ? LogEventLevel.Debug : LogEventLevel.Information)
                .WriteTo.File(Path.Combine(Directory, "simplarchive-.log"),
                    outputTemplate: Template,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: RetainedFiles,
                    shared: true)
                .CreateLogger();

            if (verbose)
            {
                Debug("Verbose console logging on (--verbose); the file at {Directory} always carries this detail", Directory);
            }
            Info("Client started ({Version} on {OS})", typeof(DesktopLog).Assembly.GetName().Version?.ToString() ?? "unknown",
                Environment.OSVersion.ToString());
        }
        catch (Exception e)
        {
            // A client that cannot write its log still has to start. A read-only home directory, a full disk, a
            // locked file — none of those is a reason to deny somebody their archive.
            Console.Error.WriteLine($"SimplArchive: logging is unavailable ({e.Message}); continuing without a log file.");
            _logger = Serilog.Core.Logger.None;
        }
    }

    /// <summary>Flushes the sinks. The crash path calls this before the process ends.</summary>
    public static void Shutdown() => (_logger as IDisposable)?.Dispose();

    public static void Debug(string template, params object?[] values) => _logger.Debug(template, values);

    public static void Info(string template, params object?[] values) => _logger.Information(template, values);

    public static void Warn(string template, params object?[] values) => _logger.Warning(template, values);

    public static void Warn(Exception e, string template, params object?[] values) => _logger.Warning(e, template, values);

    public static void Error(Exception e, string template, params object?[] values) => _logger.Error(e, template, values);

    public static void Fatal(Exception e, string template, params object?[] values) => _logger.Fatal(e, template, values);

    /// <summary>The newest log file, for the About dialog and for a support request that asks "which file?".</summary>
    public static string? NewestFile()
    {
        try
        {
            return new DirectoryInfo(Directory).EnumerateFiles("simplarchive-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
