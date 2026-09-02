namespace SimplArchive.UnitTests;

// A shell script the documentation tells someone to run as `./script.sh` must be executable, or the very
// first thing they see is "permission denied". That is a poor introduction generally and an expensive one
// for the fixed-price installers, where the operator is standing in a customer's AWS account at the time.
//
// It is easy to get wrong because it depends on how the file was created rather than on anything visible
// in review: `tools/aws-install-single/install.sh` was committed 100644 while its sibling next door was
// 100755, and nothing said so until someone ran it.
public class ShellScriptsAreExecutableTests
{
    [Fact]
    public void Every_committed_shell_script_can_be_run()
    {
        // The bit is a Unix concept; on Windows git materialises what it likes and there is nothing to
        // assert. OperatingSystem.IsWindows() rather than RuntimeInformation: only the former is a guard
        // the platform-compatibility analyzer understands, and warnings are errors here.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = RepoPaths.Root();
        var scripts = new[] { "scripts", "tools", "docs" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.sh", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"))
            .ToList();

        // Anti-vacuous: if the scan finds nothing, the assertion below proves nothing (ADR 0695).
        Assert.True(scripts.Count >= 5, $"found only {scripts.Count} shell scripts — the scan is broken, not the tree");

        // A loop rather than a LINQ Where: the platform guard above does not flow into a lambda, so the
        // analyzer rejects the call there even though it can never run on Windows.
        var notExecutable = new List<string>();
        foreach (var script in scripts)
        {
            if ((File.GetUnixFileMode(script) & UnixFileMode.UserExecute) == 0)
            {
                notExecutable.Add(Path.GetRelativePath(root, script));
            }
        }

        notExecutable.Sort(StringComparer.Ordinal);

        Assert.True(
            notExecutable.Count == 0,
            "These shell scripts are committed without the executable bit, so running them the way the "
            + $"documentation says fails with 'permission denied':{Environment.NewLine}"
            + string.Join(Environment.NewLine, notExecutable)
            + $"{Environment.NewLine}{Environment.NewLine}Fix with: git update-index --chmod=+x <path>");
    }

}
