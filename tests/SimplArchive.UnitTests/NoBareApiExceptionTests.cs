namespace SimplArchive.UnitTests;

// Anti-regression guard for the exception-type sweep (CLAUDE.md "Code style"): application code must never throw a
// bare `new ApiException("CODE", status, "message")`. Every error condition gets a dedicated, intent-named
// subclass in the two-level hierarchy under src/SimplArchive.Api/Errors/Exceptions/<Area>/. This test fails the
// build the moment a bare construction reappears anywhere under src/. (The base ApiException + the area/concrete
// subclasses only ever call `base(...)`, never `new ApiException(...)`, so the expected count is zero.)
public class NoBareApiExceptionTests
{
    [Fact]
    public void No_source_file_constructs_a_bare_ApiException()
    {
        var root = RepoRoot();

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("new ApiException("))
            .Select(f => Path.GetRelativePath(root, f))
            .OrderBy(f => f)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Throw a dedicated exception subclass under src/SimplArchive.Api/Errors/Exceptions/<Area>/ instead of a "
            + "bare `new ApiException(...)` (see the exception-type principle in CLAUDE.md). Offending files:\n"
            + string.Join("\n", offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root (SimplArchive.slnx).");
    }
}
