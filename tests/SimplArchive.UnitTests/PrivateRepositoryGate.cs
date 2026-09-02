namespace SimplArchive.UnitTests;

/// <summary>
/// Answers "is this checkout the private repository?" for the guards whose inputs are withheld from the public
/// mirror (ADR 0484) — <c>docs/</c> and <c>CLAUDE.md</c>. Those guards must stand down where their inputs do not
/// exist, and must NOT stand down anywhere else.
/// </summary>
/// <remarks>
/// <para>
/// It lives here, once, because it was three copies and every one of them carried the same blind spot (issue
/// #583 named two; the third was found while fixing them). That is the failure this file exists to prevent:
/// a gate duplicated per guard is a gate whose fix reaches some of its callers.
/// </para>
/// <para>
/// <b>The blind spot.</b> The origin was read from <c>&lt;root&gt;/.git/config</c>, and in a <b>git worktree</b>
/// <c>.git</c> is a FILE — <c>gitdir: /path/to/main/.git/worktrees/&lt;name&gt;</c> — so that path does not exist
/// and every guard returned early, passing having checked nothing. A source export has no <c>.git</c> at all and
/// standing down there is right; a worktree of the private repo IS the private repo, and development commonly
/// happens in one, because a parallel task cannot share the main checkout (a mid-run branch switch breaks a
/// running UI suite).
/// </para>
/// <para>
/// <b>Measured, not hypothetical.</b> Working in a worktree, <c>AdrIndexTests</c> reported Passed on an index
/// containing a duplicate ADR number and, later, rows out of numeric order — the exact <c>merge=union</c>
/// symptom ADR 0615 introduced it to catch. Both were found by reading the file by hand.
/// </para>
/// </remarks>
public static class PrivateRepositoryGate
{
    private const string PrivateRepo = "HebelConsulting/SimplArchivePrivate";

    /// <summary>The repository root, or null when it cannot be found — see <see cref="RepoPaths"/>.</summary>
    public static string? RepoRoot() => RepoPaths.RootOrNull();

    /// <summary>True when this checkout's origin is the private repository, resolving worktrees.</summary>
    public static bool IsPrivateRepository(string root) =>
        GitConfigPath(root) is { } config
        && File.ReadAllText(config).Contains(PrivateRepo, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>config</c> that carries the origin remote, or null when there is no git directory to consult —
    /// a source export, where standing down is correct.
    /// </summary>
    /// <remarks>
    /// Exposed so the worktree case can be exercised against a synthetic layout: the resolver's own failure
    /// mode is to return null, which every caller reads as "not the private repo" and therefore as a PASS. A
    /// guard that switches itself off silently cannot be trusted to report that it did.
    /// </remarks>
    public static string? GitConfigPath(string root)
    {
        var dotGit = Path.Combine(root, ".git");

        // An ordinary clone: .git is a directory holding config beside it.
        var direct = Path.Combine(dotGit, "config");
        if (File.Exists(direct))
        {
            return direct;
        }

        // A worktree: .git is a FILE pointing at <main>/.git/worktrees/<name>, which holds a commondir file
        // pointing back at the main .git — and the origin remote lives in the main .git's config, since a
        // worktree does not have a config of its own.
        if (!File.Exists(dotGit))
        {
            return null;
        }

        var gitDir = File.ReadAllLines(dotGit)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal))?["gitdir:".Length..]
            .Trim();

        if (string.IsNullOrEmpty(gitDir))
        {
            return null;
        }

        // The pointer may be relative to the worktree root (git writes an absolute path, but a moved or
        // hand-written checkout may not).
        if (!Path.IsPathRooted(gitDir))
        {
            gitDir = Path.GetFullPath(Path.Combine(root, gitDir));
        }

        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonDirFile))
        {
            var common = File.ReadAllText(commonDirFile).Trim();
            var mainGit = Path.IsPathRooted(common) ? common : Path.GetFullPath(Path.Combine(gitDir, common));
            var viaCommon = Path.Combine(mainGit, "config");
            if (File.Exists(viaCommon))
            {
                return viaCommon;
            }
        }

        // No commondir (an older git, or a linked checkout laid out by hand): the main .git is two levels up
        // from <main>/.git/worktrees/<name>.
        var fallback = Path.Combine(gitDir, "..", "..", "config");
        return File.Exists(fallback) ? Path.GetFullPath(fallback) : null;
    }
}
