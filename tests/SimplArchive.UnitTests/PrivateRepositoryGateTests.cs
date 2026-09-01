using Xunit;

namespace SimplArchive.UnitTests;

// The gate's failure mode is SILENCE: it returns "not the private repo", every guard behind it returns early,
// and the run is green having checked nothing (#583). So what is tested here is the resolution itself, against
// synthetic layouts -- because a guard that switches itself off cannot be trusted to report that it did.
public class PrivateRepositoryGateTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"sa-gate-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tmp))
        {
            Directory.Delete(_tmp, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string Dir(params string[] parts)
    {
        var p = Path.Combine([_tmp, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    [Fact]
    public void Finds_the_config_of_an_ordinary_clone()
    {
        var root = Dir("clone");
        var config = Path.Combine(Dir("clone", ".git"), "config");
        File.WriteAllText(config, "[remote \"origin\"]\n\turl = git@github.com:example/thing.git\n");

        Assert.Equal(config, PrivateRepositoryGate.GitConfigPath(root));
    }

    // The case that was broken. A worktree's .git is a FILE pointing at <main>/.git/worktrees/<name>, which
    // holds a commondir pointing back at the main .git -- where the origin remote actually lives, since a
    // worktree has no config of its own.
    [Fact]
    public void Finds_the_main_config_from_a_worktree()
    {
        var mainGit = Dir("main", ".git");
        var config = Path.Combine(mainGit, "config");
        File.WriteAllText(config, "[remote \"origin\"]\n\turl = git@github.com:example/thing.git\n");

        var wtAdmin = Dir("main", ".git", "worktrees", "feature");
        File.WriteAllText(Path.Combine(wtAdmin, "commondir"), "../..\n");

        var root = Dir("feature");
        File.WriteAllText(Path.Combine(root, ".git"), $"gitdir: {wtAdmin}\n");

        Assert.Equal(config, PrivateRepositoryGate.GitConfigPath(root));
    }

    // An older git, or a linked checkout laid out by hand, may have no commondir file. The admin directory is
    // still two levels under the main .git, so the resolution has a fallback rather than giving up.
    [Fact]
    public void Finds_the_main_config_from_a_worktree_with_no_commondir()
    {
        var mainGit = Dir("main", ".git");
        var config = Path.Combine(mainGit, "config");
        File.WriteAllText(config, "[remote \"origin\"]\n\turl = git@github.com:example/thing.git\n");

        var wtAdmin = Dir("main", ".git", "worktrees", "feature");
        var root = Dir("feature");
        File.WriteAllText(Path.Combine(root, ".git"), $"gitdir: {wtAdmin}\n");

        Assert.Equal(config, PrivateRepositoryGate.GitConfigPath(root));
    }

    // Standing down here is CORRECT and must stay: a source export has no git directory to consult, which is
    // exactly the public mirror's situation.
    [Fact]
    public void Stands_down_for_a_source_export_with_no_git_at_all()
    {
        Assert.Null(PrivateRepositoryGate.GitConfigPath(Dir("export")));
        Assert.False(PrivateRepositoryGate.IsPrivateRepository(Dir("export2")));
    }

    [Fact]
    public void Stands_down_when_the_pointer_leads_nowhere()
    {
        var root = Dir("broken");
        File.WriteAllText(Path.Combine(root, ".git"), $"gitdir: {Path.Combine(_tmp, "does-not-exist")}\n");

        Assert.Null(PrivateRepositoryGate.GitConfigPath(root));
    }

    // The gate reads the origin out of whatever config it resolved, so a worktree of a DIFFERENT repository
    // must not be mistaken for this one.
    [Fact]
    public void Recognises_the_private_origin_and_only_it()
    {
        var mine = Dir("mine");
        File.WriteAllText(Path.Combine(Dir("mine", ".git"), "config"),
            "[remote \"origin\"]\n\turl = git@github.com:HebelConsulting/SimplArchivePrivate.git\n");
        Assert.True(PrivateRepositoryGate.IsPrivateRepository(mine));

        var theirs = Dir("theirs");
        File.WriteAllText(Path.Combine(Dir("theirs", ".git"), "config"),
            "[remote \"origin\"]\n\turl = git@github.com:HebelConsulting/SimplArchive.git\n");
        Assert.False(PrivateRepositoryGate.IsPrivateRepository(theirs));
    }
}
