using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace SimplArchive.ConsoleLogging;

/// <summary>
/// A command-line program's output (ADRs 0906/0911, #1661): everything meant for a person goes through a logger to
/// STDERR; only a command's DATA output (what a script captures) is written to stdout, beside a comment saying so.
/// </summary>
/// <remarks>
/// <para>
/// <b>STDERR, so stdout stays the result.</b> <c>eval "$(saconsole login …)"</c> executes stdout and
/// <c>id=$(saconsole repository create …)</c> captures it, so a progress line there is either run or kept as the id.
/// <c>2&gt;/dev/null</c> silences the person's half and keeps the result.
/// </para>
/// <para>
/// <b>Plain lines</b> (<see cref="PlainConsoleFormatter"/>): Information is just the message, Warning and Error carry a
/// <c>warning:</c>/<c>error:</c> prefix, Debug and Trace appear only when <c>verbose</c>.
/// </para>
/// <para>
/// <b>Dispose the factory before exit.</b> The console logger writes on a background thread, so a factory that is not
/// disposed loses the last lines, which on a failure are the only lines.
/// </para>
/// <para>
/// <b>One implementation for every console program</b> (ADR 0877): saconsole, the tools under <c>tools/</c> and the
/// published test tools share this rather than each growing a formatter of its own.
/// </para>
/// </remarks>
public static class PlainConsoleLogging
{
    /// <summary>The conventional switch for Debug and Trace lines, for a program that offers one.</summary>
    public const string VerboseFlag = "--verbose";

    public static ILoggerFactory CreateFactory(bool verbose = false) => LoggerFactory.Create(builder => builder
        .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information)
        .AddConsole(options =>
        {
            options.FormatterName = PlainConsoleFormatter.FormatterName;
            options.LogToStandardErrorThreshold = LogLevel.Trace;   // every level: stdout is for data
        })
        .AddConsoleFormatter<PlainConsoleFormatter, ConsoleFormatterOptions>());
}

/// <summary>The message alone, with a level prefix only for warnings and errors.</summary>
public sealed class PlainConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "plain";

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        var prefix = logEntry.LogLevel switch
        {
            LogLevel.Warning => "warning: ",
            LogLevel.Error or LogLevel.Critical => "error: ",
            _ => string.Empty,
        };
        textWriter.WriteLine($"{prefix}{message}");
    }
}
