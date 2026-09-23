using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

/// <summary>
/// Every sidecar WE publish must be in the set <c>rolling-update.sh</c> rolls.
///
/// The failure this guards is the one that produced the rule: a release updated api and api-b and left the
/// encryption sidecar on a pre-rotation image, so the core called endpoints that did not exist while
/// <c>docker compose ps</c> looked entirely healthy — the image tag was the only place the drift showed.
/// The fix is a LIST, and a list is exactly the thing that goes stale: add a fourth self-published sidecar
/// and it is silently not rolled, reproducing the original defect with the remedy already in the tree.
///
/// Third-party images are deliberately not asserted. They are version-pinned and restarting the stateful
/// ones would interrupt the demo, which is the one thing the rolling update promises not to do.
///
/// Private-repository only: <c>tools/</c> is withheld from the public mirror (ADR 0484), so the input does
/// not exist there. It is required here and fails loudly rather than skipping.
/// </summary>
public sealed partial class SidecarRollLockstepTests
{
    /// <summary>The two app instances are rolled by their own one-at-a-time loop, not as sidecars.</summary>
    private static readonly string[] AppInstances = ["api", "api-b"];

    [Fact]
    public void Every_self_published_sidecar_is_rolled_by_the_update_script()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root || !PrivateRepositoryGate.IsPrivateRepository(root))
        {
            return; // the public mirror carries no tools/ — nothing to check
        }

        var script = Path.Combine(root, "scripts", "rolling-update.sh");
        Assert.True(File.Exists(script), $"{script} is missing — the rolling update is what this asserts about.");

        var declared = SidecarsDefault().Match(File.ReadAllText(script));
        Assert.True(declared.Success,
            "rolling-update.sh no longer declares a SIDECARS default. If the sidecar roll moved or was "
            + "removed, this guard must move with it rather than be deleted — the drift it catches is silent.");

        var rolled = declared.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

        var kioskDirectory = Path.Combine(root, "tools", "kiosk");
        var ours = Directory.EnumerateFiles(kioskDirectory, "docker-compose*.yml")
            .SelectMany(file => OurServices(File.ReadAllText(file)))
            .Where(service => !AppInstances.Contains(service, StringComparer.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(ours); // an empty expectation would pass vacuously

        var missing = ours.Except(rolled).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            $"These sidecars are built from our own repositories but are NOT rolled by rolling-update.sh: "
            + $"{string.Join(", ", missing)}. A release would update the app and leave them on whatever they "
            + "were already running, which reads as a healthy stack at the wrong version. Add them to the "
            + "SIDECARS default, or say at the service why it must not be rolled.");
    }

    /// <summary>The literal default in <c>SIDECARS=${SA_SIDECARS:-"..."}</c>.</summary>
    [GeneratedRegex(@"SIDECARS=\$\{SA_SIDECARS:-""([^""]*)""\}")]
    private static partial Regex SidecarsDefault();

    /// <summary>
    /// Service names whose image we publish ourselves. Matches a service key followed, within its block, by
    /// an image on our registry — the same "ours" test PinnedImageLockstepTests uses.
    /// </summary>
    private static IEnumerable<string> OurServices(string yaml)
    {
        foreach (Match m in ServiceWithOurImage().Matches(yaml))
        {
            yield return m.Groups[1].Value;
        }
    }

    [GeneratedRegex(@"^  ([a-z0-9][a-z0-9_-]*):\s*$(?:\r?\n(?:    .*)?$)*?\r?\n    image:\s*ghcr\.io/hebelconsulting/",
        RegexOptions.Multiline)]
    private static partial Regex ServiceWithOurImage();
}
