using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// ADR 0917 (#1687): the mobile app consumes SimplArchive.ApiClient as a PACKAGE, and a package restores only if
// every package it depends on was published too. So the publish workflow must pack exactly ApiClient and the core
// projects it references, transitively, and watch their paths. A new ProjectReference that the workflow does not
// know would publish an ApiClient the mobile app cannot restore, on a green run.
public partial class ClientPackageLockstepTests
{
    private const string Workflow = ".github/workflows/client-packages-publish.yml";

    [Fact]
    public void The_publish_workflow_packs_and_watches_exactly_ApiClient_and_what_it_references()
    {
        // The workflow is withheld from the public mirror (it publishes from the canonical repository only), so there it
        // is absent BY DESIGN. Asked of the ORIGIN, as AdrIndexTests does, never of the file's existence, which would
        // also excuse a workflow somebody deleted here. Found when the mirror's own unit tier refused the publish.
        if (PrivateRepositoryGate.RepoRoot() is not { } gate || !PrivateRepositoryGate.IsPrivateRepository(gate))
        {
            return;
        }

        var root = RepoPaths.Root();
        var expected = Closure(root, "SimplArchive.ApiClient");
        var workflow = File.ReadAllText(Path.Combine(root, Workflow));

        var packed = PackLoop().Match(workflow).Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var watched = WatchedPath().Matches(workflow).Select(m => m.Groups[1].Value).ToHashSet();

        Assert.Equal(expected.Order(), packed.Order());
        Assert.Equal(expected.Order(), watched.Order());
    }

    [Fact]
    public void Every_published_project_carries_the_shared_package_metadata()
    {
        var root = RepoPaths.Root();
        var missing = Closure(root, "SimplArchive.ApiClient")
            .Where(p => !File.ReadAllText(Path.Combine(root, "src", p, $"{p}.csproj")).Contains("build\\ClientPackages.props", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0, $"Import build/ClientPackages.props in: {string.Join(", ", missing)}");
    }

    private static HashSet<string> Closure(string root, string project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([project]);
        while (stack.TryPop(out var current))
        {
            if (!seen.Add(current))
            {
                continue;
            }

            var csproj = File.ReadAllText(Path.Combine(root, "src", current, $"{current}.csproj"));
            foreach (Match reference in ProjectReference().Matches(csproj))
            {
                stack.Push(reference.Groups[1].Value);
            }
        }

        return seen;
    }

    [GeneratedRegex(@"ProjectReference Include=""[^""]*?(SimplArchive\.\w+)\.csproj""")]
    private static partial Regex ProjectReference();

    [GeneratedRegex(@"for project in ([\w. ]+); do")]
    private static partial Regex PackLoop();

    [GeneratedRegex(@"- ""src/(SimplArchive\.\w+)/\*\*""")]
    private static partial Regex WatchedPath();
}
