using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// A personal space is named after its OWNER (ADR 0671). Four call sites across the two clients still spelled
// out the literal "Personal/…", so every one of them addressed a folder that does not exist: the desktop's
// "open WebDAV folder" silently did nothing, and the web copied a link that could not resolve.
//
// Nothing failed loudly, which is why it survived — a path to a missing folder looks identical to a path that
// works until someone follows it.
public class WebDavPersonalSpaceNameTests
{
    [Fact]
    public void A_personal_folder_is_addressed_under_the_owners_own_name()
        => Assert.Equal("Demo Admin/Intray", WebDavPaths.InPersonalSpace("Demo Admin", "Intray"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unknown_name_yields_the_mount_root_rather_than_a_guess(string? name)
    {
        // Empty means "open the mount root", which every caller already handles. The alternative — falling back
        // to some literal — is what this bug WAS: a confident path to a folder that is not there. One level up
        // is navigable; a wrong path just fails.
        Assert.Equal(string.Empty, WebDavPaths.InPersonalSpace(name, "Intray"));
    }

    [Fact]
    public void Slashes_around_the_parts_do_not_double_up()
        => Assert.Equal("Demo Admin/Check-out", WebDavPaths.InPersonalSpace("/Demo Admin/", "/Check-out"));

    [Fact]
    public void No_client_still_spells_out_the_old_literal()
    {
        // The guard that matters. Both clients had it, and a fix in one would otherwise leave the other broken
        // — the divergence ADR 0511 exists to prevent. Comments are stripped so the ones EXPLAINING the trap
        // do not trip it.
        var offenders = new List<string>();
        foreach (var dir in new[] { "SimplArchive.Client", "SimplArchive.DesktopClient" })
        {
            var root = Path.Combine(RepoRoot(), "src", dir);
            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".razor", StringComparison.Ordinal)
                                     || f.EndsWith(".cs", StringComparison.Ordinal)
                                     || f.EndsWith(".axaml", StringComparison.Ordinal))
                         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                var code = string.Join('\n', File.ReadAllLines(file)
                    .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                                && !l.TrimStart().StartsWith("<!--", StringComparison.Ordinal)
                                && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));

                if (code.Contains("\"Personal/", StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetFileName(file));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A personal space is named after its owner (ADR 0671), so a hardcoded \"Personal/…\" path addresses "
            + "a folder that does not exist. Compose it with WebDavPaths.InPersonalSpace instead:\n  "
            + string.Join("\n  ", offenders));
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
