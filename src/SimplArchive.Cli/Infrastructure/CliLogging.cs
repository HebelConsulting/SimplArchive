using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.ConsoleLogging;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace SimplArchive.Cli.Infrastructure;

/// <summary>
/// saconsole's Spectre-specific output helpers. The logger itself (stderr, plain lines, <c>--verbose</c>) is the shared
/// <see cref="PlainConsoleLogging"/> every console program uses (ADRs 0906/0911, #1661); what stays here is what only a
/// Spectre program needs.
/// </summary>
public static class CliLogging
{
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
