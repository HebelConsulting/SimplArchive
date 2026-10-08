using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// The standing principle "output goes through a logger, never the console" (ADR 0906), as a ratchet.
//
// WHAT IT COUNTS. Every line that writes to the console directly: System.Console's Write/WriteLine (on Out and
// Error too), and Spectre's AnsiConsole and IAnsiConsole Write/Markup calls. Comments are skipped. It scans what
// ships or runs as a tool: src/, tools/ and the two published test tools (SelfHosting, ManualCapture).
//
// WHAT IS ALLOWED TO STAY. One kind of site only: a command-line tool's DATA output, the RESULT it prints for a
// script to capture (`eval "$(saconsole login …)"`, `id=$(saconsole repository create …)`). A log line's timestamp
// and level would break the capture, so that write stays plain stdout. Everything else (progress, diagnostics,
// the human half of a CLI, the desktop's headless hooks) goes through ILogger with a console provider.
//
// THE NUMBERS BELOW ARE THE DEBT at the moment the principle was adopted (2026-10-08: 371 lines in 42 files;
// Api, Infrastructure and Worker were already clean). They only go DOWN: convert a file, lower its number in the
// same commit, and delete the entry at zero. A count that went UP is new console output; write it through a
// logger instead. A genuine data-output site keeps its line and says so beside the call.
public class ConsoleOutputRatchetTests
{
    private static readonly Regex ConsoleWrite = new(
        @"\bConsole\s*\.\s*(?:Out\s*\.\s*|Error\s*\.\s*)?Write(?:Line)?\s*\("
        + @"|\bAnsiConsole\s*\.\s*(?:Write(?:Line)?|Markup(?:Line)?(?:Interpolated)?)\s*\("
        + @"|\.\s*Markup(?:Line)?(?:Interpolated)?\s*\(",
        RegexOptions.Compiled);

    private static readonly string[] Roots = ["src", "tools", "tests/SimplArchive.SelfHosting", "tests/SimplArchive.ManualCapture"];

    private static readonly Dictionary<string, int> Remaining = new()
    {
        ["src/SimplArchive.Cli/Commands/LoginCommand.cs"] = 2,   // data output only (ADR 0906)
        ["src/SimplArchive.Cli/Commands/RepositoryCreateCommand.cs"] = 1,   // data output only (ADR 0906)
        ["src/SimplArchive.Cli/Commands/StandardRepositoryCommands.cs"] = 1,   // data output: the id (#1661; was an uncounted console.WriteLine)
        ["src/SimplArchive.Cli/Commands/TenantCreateCommand.cs"] = 1,   // data output only (ADR 0906)
        ["src/SimplArchive.Client/Pages/Home.Interop.razor.cs"] = 3,
        ["src/SimplArchive.DesktopClient/Program.cs"] = 47,
        ["src/SimplArchive.DesktopClient/Services/ApiClientChecks.cs"] = 52,
        ["src/SimplArchive.DesktopClient/Services/CardEnvelopeCheck.cs"] = 19,
        ["src/SimplArchive.DesktopClient/Services/DesktopLog.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/DiffViewCheck.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/IconWriter.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Views/ColumnDragCheck.cs"] = 13,
        ["src/SimplArchive.DesktopClient/Views/DatePickerBindingCheck.cs"] = 3,
        ["src/SimplArchive.DesktopClient/Views/IndexScrollCheck.cs"] = 8,
        ["src/SimplArchive.DesktopClient/Views/ListScrollCheck.cs"] = 4,
        ["src/SimplArchive.DesktopClient/Views/OpenShortcutCheck.cs"] = 4,
        ["src/SimplArchive.DesktopClient/Views/ScreenshotRenderer.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Views/SearchFieldCheck.cs"] = 3,
        ["src/SimplArchive.DesktopClient/Views/SortThumbnailsCheck.cs"] = 2,
        ["tests/SimplArchive.ManualCapture/DesktopCapture.cs"] = 1,
        ["tests/SimplArchive.ManualCapture/LiveDesktopCapture.cs"] = 3,
        ["tests/SimplArchive.ManualCapture/Program.cs"] = 2,
        ["tests/SimplArchive.ManualCapture/WebCapture.cs"] = 18,
        ["tests/SimplArchive.SelfHosting/SelfHostedApp.cs"] = 1,
    };

    /// <summary>
    /// The allowances: src/ and the published test tools from the table above, plus tools/ from
    /// <c>tools/console-output-ratchet.tsv</c>. That file lives under tools/ because tools/ is withheld from the public
    /// mirror, and this test file is published (some tool names may not appear in a published file).
    /// </summary>
    private static Dictionary<string, int> Allowances(string root)
    {
        var all = new Dictionary<string, int>(Remaining, StringComparer.Ordinal);
        var tsv = Path.Combine(root, "tools", "console-output-ratchet.tsv");
        if (PrivateRepositoryGate.IsPrivateRepository(root))
        {
            Assert.True(File.Exists(tsv), $"{tsv} is missing: the tools/ half of this ratchet lives there.");
            foreach (var line in File.ReadLines(tsv).Where(l => l.Length > 0 && l[0] != '#'))
            {
                var parts = line.Split('\t');
                all[parts[1]] = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return all;
    }

    [Fact]
    public void No_file_writes_to_the_console_more_than_it_did_and_a_clean_file_stays_clean()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var counts = Count(root);
        var allowed = Allowances(root);
        var grew = counts
            .Where(c => c.Value > allowed.GetValueOrDefault(c.Key))
            .Select(c => $"  {c.Key}: {c.Value} (allowed {allowed.GetValueOrDefault(c.Key)})")
            .ToList();

        Assert.True(grew.Count == 0,
            "Console output grew (ADR 0906). Write it through an ILogger with a console provider instead. "
            + "Only a command-line tool's DATA output (what a script captures) may write stdout directly:\n"
            + string.Join("\n", grew));
    }

    [Fact]
    public void A_file_that_paid_its_debt_lowers_its_number()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var counts = Count(root);
        var stale = Allowances(root)
            .Where(r => counts.GetValueOrDefault(r.Key) < r.Value)
            .Select(r => $"  {r.Key}: allowed {r.Value}, now {counts.GetValueOrDefault(r.Key)}")
            .ToList();

        Assert.True(stale.Count == 0,
            "These files write to the console less than their allowance. Lower the number (or delete the entry "
            + "at zero) in the same change, so the debt cannot quietly grow back:\n" + string.Join("\n", stale));
    }

    private static Dictionary<string, int> Count(string root)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var dir in Roots.Select(r => Path.Combine(root, r)).Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                var n = File.ReadLines(file).Count(line =>
                    !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && ConsoleWrite.IsMatch(line));
                if (n > 0)
                {
                    counts[Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')] = n;
                }
            }
        }

        return counts;
    }
}
