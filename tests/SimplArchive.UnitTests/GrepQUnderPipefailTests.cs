using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

/// <summary>
/// No shell script may pipe into <c>grep -q</c> while <c>pipefail</c> is set.
///
/// The shape looks obviously correct and is not. <c>grep -q</c> exits at its FIRST match, which SIGPIPEs
/// whatever feeds it; under <c>set -o pipefail</c> the pipeline then reports that signal instead of grep's
/// success, so a match reads as a non-match — at random, depending on whether the producer had finished
/// writing. Measured on one host over 20 attempts per service: 5/20, 16/20 and 6/20 wrong.
///
/// It is worth a guard rather than a comment because every wrong answer is SILENT and the failure is
/// intermittent: it disabled the rolling update's module-install step on every release for an unknown
/// period, while the run reported success. Capture the output first and match it without a pipe.
///
/// Comment lines are skipped deliberately — the fixes explain the trap, and a guard that punishes
/// documenting the rule teaches people to stop documenting it.
///
/// Workflows are scanned too, with no need to find a `set` line: GitHub's default shell is
/// <c>bash --noprofile --norc -eo pipefail</c>, so every <c>run:</c> block is already subject to this. That
/// gap is why <c>ci.yml</c> kept the shape after every script had been fixed.
/// </summary>
public sealed partial class GrepQUnderPipefailTests
{
    [Fact]
    public void No_script_pipes_into_grep_q_while_pipefail_is_set()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var offenders = new List<string>();

        var workflows = Path.Combine(root, ".github", "workflows");
        var files = Directory.EnumerateFiles(root, "*.sh", SearchOption.AllDirectories)
            .Concat(Directory.Exists(workflows) ? Directory.EnumerateFiles(workflows, "*.yml") : []);

        foreach (var file in files)
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);

            // A workflow needs no `set` line: GitHub's default bash shell already sets pipefail.
            var isWorkflow = file.StartsWith(workflows, StringComparison.Ordinal);
            if (!isWorkflow && !lines.Any(l => Pipefail().IsMatch(l)))
            {
                continue; // without pipefail the pipeline reports grep's status, which is correct
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith('#'))
                {
                    continue; // prose about the trap is not the trap
                }

                if (PipeIntoGrepQ().IsMatch(line))
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These pipe into `grep -q` under pipefail, where a MATCH can be reported as a failure because "
            + "grep exits early and SIGPIPEs its producer — silently, and only sometimes:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nCapture the output into a variable first, then match it with `case` or a here-string.");
    }

    [GeneratedRegex(@"^\s*set\s+-[a-zA-Z]*\s*(-o\s+pipefail|.*\bpipefail\b)")]
    private static partial Regex Pipefail();

    [GeneratedRegex(@"\|\s*grep\s+(-[a-zA-Z]*q[a-zA-Z]*)\b")]
    private static partial Regex PipeIntoGrepQ();
}
