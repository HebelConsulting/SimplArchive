using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// The two E2E fixtures each stand up their own OpenSearch container, and their definitions are copies. That is
// the drift this guards, because the copies already cost a day (#663).
//
// Two things must hold, and neither is visible from either file alone:
//
//   1. The image is PINNED, to the same version the shipped stack runs. It was `opensearchproject/opensearch:2`
//      — a floating tag — so a green build went red with no commit on our side, and the suite was not testing
//      the version that ships anyway.
//
//   2. Index State Management is DISABLED. Nothing here uses it, and its start-up template migration sets
//      `cluster.blocks.create_index` on the cluster while it runs. Measured on 2.19.6: /_cluster/health answers
//      200 at t≈1s and the migration runs at t≈50-60s, so every index created in that window is refused with a
//      403 and search then answers zero hits forever — the app healthy, the cluster green, every shard assigned.
//      A clear-the-block-at-boot workaround was tried first and is exactly what the timings above rule out: it
//      ran a minute before the plugin had done anything, which is why three of five CI legs failed and two
//      passed on timing luck. Prevention, not a better-timed clear.
public class OpenSearchContainerParityTests
{
    private const string ExpectedTag = "2.19.6";

    private static readonly string[] FixtureFiles =
    [
        Path.Combine("tests", "SimplArchive.SelfHosting", "SelfHostedApp.cs"),
        Path.Combine("tests", "SimplArchive.EndToEndTests", "E2EApiFactory.cs"),
    ];

    // Matches the tag in either a .WithImage("…") call or a compose/Helm `image:` line.
    private static readonly Regex ImageTag = new(@"opensearchproject/opensearch:([^""\s]+)", RegexOptions.Compiled);

    [Fact]
    public void Both_test_fixtures_pin_the_same_OpenSearch_version()
    {
        var root = RepoRoot();

        foreach (var file in FixtureFiles)
        {
            var text = ReadRequired(root, file);
            var match = ImageTag.Match(text);

            Assert.True(match.Success, $"{file} no longer names an opensearchproject/opensearch image — this guard has gone blind; fix the pattern rather than deleting the test.");
            Assert.True(
                match.Groups[1].Value == ExpectedTag,
                $"{file} runs opensearchproject/opensearch:{match.Groups[1].Value}, not the pinned {ExpectedTag}. "
                + "A floating tag means an image change lands as an unexplained red build (#663); bump both fixtures "
                + "and this constant together, deliberately.");
        }
    }

    // Both routes to the same 403, guarded together because a fixture that closes one and not the other still
    // loses the whole suite — and the two are indistinguishable from the failure, which is what cost the day.
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
        var root = RepoRoot();

        foreach (var file in FixtureFiles)
        {
            var text = ReadRequired(root, file);

            // The setting AND its value, on one line: asserting the key alone would pass on a fixture that
            // mentions it in a comment, or that sets it to "true".
            Assert.True(
                text.Split('\n').Any(l => l.Contains(setting, StringComparison.Ordinal) && l.Contains("\"false\"", StringComparison.Ordinal)),
                $"{file} {consequence} (#663).");
        }
    }

    // The claim the pin makes is that the suite tests what ships, so the shipped stack has to agree. Each file is
    // checked only if present: docs/ and tools/ are withheld from the public mirror while tests/ is published
    // byte-for-byte (ADR 0484), so a test that REQUIRED a withheld file would fail there by construction.
    [Fact]
    public void The_shipped_stack_runs_the_version_the_tests_pin()
    {
        var root = RepoRoot();
        string[] shipped =
        [
            "docker-compose.yaml",
            Path.Combine("charts", "simplarchive", "values.yaml"),
        ];

        var checkedAny = false;
        foreach (var file in shipped)
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (Match match in ImageTag.Matches(File.ReadAllText(path)))
            {
                checkedAny = true;
                Assert.True(
                    match.Groups[1].Value == ExpectedTag,
                    $"{file} runs opensearchproject/opensearch:{match.Groups[1].Value} while the tests pin "
                    + $"{ExpectedTag}. The suite would then be proving a version nobody deploys.");
            }
        }

        Assert.True(checkedAny, "No shipped stack file named an OpenSearch image — this guard has gone blind.");
    }

    private static string ReadRequired(string root, string file)
    {
        var path = Path.Combine(root, file);
        Assert.True(File.Exists(path), $"{file} has moved; update this guard to follow it rather than letting it pass on a missing file.");
        return File.ReadAllText(path);
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
