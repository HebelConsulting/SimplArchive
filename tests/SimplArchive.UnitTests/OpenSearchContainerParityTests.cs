namespace SimplArchive.UnitTests;

// The two E2E fixtures each stand up their own OpenSearch container, and their definitions are copies. That is
// the drift this guards, because the copies already cost a day (#663).
//
// WHAT THIS FILE NO LONGER DOES, and why. It used to assert two things: that the image is pinned to the version
// the shipped stack runs, and that Index State Management is disabled. The first is now
// PinnedImageLockstepTests' job, for EVERY third-party image rather than this one — which is the point, because
// this guard was written for OpenSearch after the day it cost, while `apache/tika:latest-full` floated on
// untouched in both fixtures for months afterwards. A guard covering one instance of a general mistake reads as
// if the mistake is handled.
//
// It also held its own `ExpectedTag = "2.19.6"` — a FIFTH copy of a version that already existed in four
// places. images.env is the single source now, and the fixtures read it, so the copy is gone with the tests
// that needed it.
//
// The version half also went BLIND on the way there, which is the sharper reason not to leave it here: wiring
// the fixtures to `ImagePins.Image("opensearchproject/opensearch", "OPENSEARCH_TAG")` removed the `repo:tag`
// literal its regex required, and the test caught that only because it asserted its own non-blindness. A
// narrower guard duplicating a broader one is a second thing to keep working, for no coverage.
//
// WHAT REMAINS IS THIS FILE'S ALONE: the two cluster settings. Nothing else asserts them, and neither is
// visible from either fixture in isolation.
public class OpenSearchContainerParityTests
{
    private static readonly string[] FixtureFiles =
    [
        Path.Combine("tests", "SimplArchive.SelfHosting", "SelfHostedApp.cs"),
        Path.Combine("tests", "SimplArchive.EndToEndTests", "E2EApiFactory.cs"),
    ];

    // Both routes to the same 403, guarded together because a fixture that closes one and not the other still
    // loses the whole suite — and the two are indistinguishable from the failure, which is what cost the day.
    //
    // Index State Management's start-up template migration sets `cluster.blocks.create_index` on the cluster
    // while it runs. Measured on 2.19.6: /_cluster/health answers 200 at t≈1s and the migration runs at
    // t≈50-60s, so every index created in that window is refused with a 403 and search then answers zero hits
    // forever — the app healthy, the cluster green, every shard assigned. A clear-the-block-at-boot workaround
    // was tried first and is exactly what those timings rule out: it ran a minute before the plugin had done
    // anything, which is why three of five CI legs failed and two passed on timing luck. Prevention, not a
    // better-timed clear.
    [Theory]
    [InlineData(
        "cluster.routing.allocation.disk.threshold_enabled",
        "does not disable OpenSearch's disk watermarks. A node above the HIGH watermark makes OpenSearch set "
        + "cluster.blocks.create_index, and a hosted runner sits at 93% full before the fleet starts — so every "
        + "index creation 403s and search answers zero hits forever")]
    [InlineData(
        "plugins.index_state_management.enabled",
        "does not disable Index State Management. Its start-up template migration sets cluster.blocks.create_index "
        + "at t≈50-60s, long after /_cluster/health answers at t≈1s, so an index created in that window is refused")]
    public void Both_test_fixtures_close_each_route_to_a_create_index_block(string setting, string consequence)
    {
        var root = RepoPaths.Root();

        foreach (var file in FixtureFiles)
        {
            var path = Path.Combine(root, file);
            Assert.True(File.Exists(path), $"{file} has moved; update this guard to follow it rather than letting it pass on a missing file.");
            var text = File.ReadAllText(path);

            // The setting AND its value, on one line: asserting the key alone would pass on a fixture that
            // mentions it in a comment, or that sets it to "true".
            Assert.True(
                text.Split('\n').Any(l => l.Contains(setting, StringComparison.Ordinal) && l.Contains("\"false\"", StringComparison.Ordinal)),
                $"{file} {consequence} (#663).");
        }
    }
}
