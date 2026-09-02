using Xunit;

namespace SimplArchive.UnitTests;

// The whitespace gate (ADRs 0439/0440) runs `dotnet format whitespace`, which does NOT see .razor or .axaml --
// leaving 88 Razor components (~19,900 lines) and 99 Avalonia views outside it, against 1,306 .cs files inside
// (issue #935). That was established rather than assumed: with a trailing-space and a wrong-indent defect
// injected into a .razor @code block, the gate exits 0; it still exits 0 with the offending file named in an
// explicit --include, and `-v diag` never mentions the file at all. So this is not a switch the repo failed to
// turn on -- the tool cannot cover these extensions in the pinned SDK -- and a narrow guard is the alternative
// the issue leaves.
//
// WHY IT MATTERS: the point of a formatting gate is not neatness, it is that a mangled line is a SIGNAL. The
// three lines that exposed this read as a botched automated edit, and that is what they were -- a real (latent)
// control-flow bug, #933. A .razor @code block is C# that nothing formats.
//
// WHAT THIS DOES NOT COVER, stated plainly so the guard is not mistaken for the gate: only the four defects
// that carry no formatting OPINION are checked, because these files are markup and nobody has agreed a
// formatter for them. In particular a line indented to the WRONG LEVEL -- #933's actual defect -- has correct
// characters and is invisible here. A guard that claimed otherwise would be worse than this one.
public class MarkupWhitespaceTests
{
    private static readonly string[] Extensions = [".razor", ".axaml"];

    [Fact]
    public void No_markup_file_has_whitespace_dotnet_format_would_reject_in_a_cs_file()
    {
        var root = RepoPaths.Root();
        var problems = new List<string>();

        foreach (var tree in new[] { "src", "tests", "tools" })
        {
            var treePath = Path.Combine(root, tree);
            if (!Directory.Exists(treePath))
            {
                // tools/ is withheld from the public mirror (ADR 0484). Nothing to scan, by design.
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(treePath, "*", SearchOption.AllDirectories))
            {
                if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                    || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                var bytes = File.ReadAllBytes(path);
                var text = File.ReadAllText(path);

                if (bytes.Length > 0 && bytes[^1] != (byte)'\n')
                {
                    problems.Add($"{rel}: no final newline");
                }

                if (text.Contains("\r\n", StringComparison.Ordinal))
                {
                    problems.Add($"{rel}: CRLF line endings (the repository is LF)");
                }

                var lines = text.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    // The last element after a trailing \n is the empty string, not a line.
                    if (i == lines.Length - 1 && lines[i].Length == 0)
                    {
                        continue;
                    }

                    var line = lines[i].TrimEnd('\r');
                    if (line.Length > 0 && line.TrimEnd() != line)
                    {
                        problems.Add($"{rel}:{i + 1}: trailing whitespace");
                    }

                    var indent = line[..(line.Length - line.TrimStart().Length)];
                    if (indent.Contains('\t', StringComparison.Ordinal))
                    {
                        problems.Add($"{rel}:{i + 1}: tab in the indentation (this repository indents with spaces)");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0,
            $"{problems.Count} whitespace defect(s) in markup files that `dotnet format whitespace` cannot see "
            + "(#935). These are the defects that carry no formatting opinion — trailing whitespace, tabs in the "
            + "indentation, a missing final newline, CRLF — so fixing them is safe and mechanical:\n  "
            + string.Join("\n  ", problems.Take(50))
            + (problems.Count > 50 ? $"\n  … and {problems.Count - 50} more" : string.Empty));
    }

    // Anti-vacuous. A scan that silently matched nothing would pass forever, which is the shape of the very
    // problem being fixed: a gate that is green because it is looking at an empty set.
    [Fact]
    public void The_scan_actually_reaches_the_markup()
    {
        var root = RepoPaths.Root();
        var counted = Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
            .Where(p => Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Count(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        Assert.True(counted > 150,
            $"Only {counted} markup files found under src/ — there were 187 (88 .razor + 99 .axaml) when this "
            + "guard was written. Either the scan stopped matching, or the client moved; check before relaxing.");
    }

}
