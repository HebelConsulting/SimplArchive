using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace SimplArchive.Cli.Infrastructure;

/// <summary>
/// saconsole's output (ADR 0906, #1661): everything meant for a person goes through a logger to STDERR; only a
/// command's DATA output (what a script captures) is written to stdout, beside a comment saying so.
/// </summary>
/// <remarks>
/// <para>
/// <b>STDERR, so stdout stays the result.</b> <c>eval "$(saconsole login …)"</c> executes stdout and
/// <c>id=$(saconsole repository create …)</c> captures it, so a progress line there is either run or kept as the id.
/// <c>2&gt;/dev/null</c> silences the person's half and keeps the result.
/// </para>
/// <para>
/// <b>Plain lines</b> (<see cref="PlainConsoleFormatter"/>): Information is just the message, Warning and Error carry a
/// <c>warning:</c>/<c>error:</c> prefix, Debug and Trace appear only with <c>--verbose</c>.
/// </para>
/// <para>
/// <b>Disposed before exit.</b> The console logger writes on a background thread, so a factory that is not disposed loses
/// the last lines, which on a failure are the only lines.
/// </para>
/// </remarks>
public static class CliLogging
{
    public const string VerboseFlag = "--verbose";

    public static ILoggerFactory CreateFactory(bool verbose) => LoggerFactory.Create(builder => builder
        .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information)
        .AddConsole(options =>
        {
            options.FormatterName = PlainConsoleFormatter.FormatterName;
            options.LogToStandardErrorThreshold = LogLevel.Trace;   // every level: stdout is for data
        })
        .AddConsoleFormatter<PlainConsoleFormatter, ConsoleFormatterOptions>());

    /// <summary>A table as plain text, for logging as one entry: no colours, no wrapping a log line would break.</summary>
    public static string Render(IRenderable table)
    {
        var text = new StringWriter();
        var plain = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(text),
        });
        plain.Profile.Width = 200;
        plain.Write(table);
        return text.ToString().TrimEnd();
    }

    /// <summary>For a caller (a test) that wants the work and not the words.</summary>
    public static ILogger Silent => NullLogger.Instance;
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
