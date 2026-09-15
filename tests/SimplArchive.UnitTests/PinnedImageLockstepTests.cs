using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// Every place that names a third-party service image must name the SAME version, and must PIN it.
//
// WHY. A third-party image — Postgres, OpenSearch, Tika, Gotenberg, Valkey, SeaweedFS — is named in four
// places: `docker-compose.yaml`, `charts/simplarchive/values.yaml`, and the two E2E fixtures that stand the
// same services up with Testcontainers. A floating tag in a FIXTURE means the suite is not testing the version
// that ships, and nobody learns that until the upstream project releases.
//
// That is not a prediction. `opensearchproject/opensearch:2` floated, a green build went red with no commit on
// our side, and the day it cost bought `OpenSearchContainerParityTests` — a guard written for ONE image while
// the identical mistake sat untouched in the others. Measured when this file was written, all four places
// disagreed: Postgres was 16.15-alpine / 16.14-alpine / 16-alpine / 16-alpine, Gotenberg 8.37 / 8.34.0 / 8 / 8,
// and `apache/tika:latest-full` floated in BOTH fixtures — on the component that extracts the text search
// depends on.
//
// WHAT A GREEN RUN IS WORTH, which is the real point: a test run proves the shipped stack works only if it runs
// the shipped stack's versions. Otherwise it proves that SOME version works, which is a weaker claim than
// anybody reads it as, and the gap surfaces as a production-only failure nobody can reproduce locally.
//
// THE KIOSK IS NOT CHECKED HERE, deliberately. `tools/kiosk/docker-compose.yml` is the outward-facing demo and
// moves at ROLLOUT (docs/deploy/release.md), so it lags on purpose. Its drift is reported per file by
// scripts/check-pinned-images.sh — visible rather than silent — and closed by the rollout, not by a dev-stack
// commit. Folding it in here would make this guard permanently red for a lag that is a decision.
public partial class PinnedImageLockstepTests
{
    /// <summary>An image reference in compose/chart YAML: `image: repo:tag` or `image: repo@sha256:…`.</summary>
    /// <remarks>
    /// Handles both spellings: a literal <c>repo:tag</c> and compose's <c>repo:${VAR:-tag}</c>. The DEFAULT
    /// inside the substitution is the copy that matters — it is what runs when no env file is supplied, which
    /// is how the documented `docker compose up --build` starts the stack.
    /// </remarks>
    [GeneratedRegex(@"image:\s*([a-z0-9][a-z0-9./-]*)(?::\$\{[A-Z_]+:-([^}]+)\}|:([^\s""'${]+)|@(sha256:[a-f0-9]+))")]
    private static partial Regex YamlImage();

    /// <summary>An image reference in a C# fixture: `ImagePins.Image("repo", "VAR")`, or a legacy `repo:tag`.</summary>
    /// <remarks>
    /// BOTH forms, and the first one is why this guard nearly went blind on the surface it exists for. Wiring
    /// the fixtures to images.env replaced every `"apache/tika:latest-full"` with
    /// `ImagePins.Image("apache/tika", "TIKA_TAG")` — no `repo:tag` literal left to match. The tag-only pattern
    /// then found NOTHING in either fixture, and the anti-vacuous check passed anyway because it counted the
    /// TOTAL across four files, which compose and the chart satisfy between them. A guard that stops seeing one
    /// surface while still going green is worse than no guard, so the count is now asserted PER SURFACE.
    /// </remarks>
    [GeneratedRegex(@"ImagePins\.Image\(""([a-z0-9][a-z0-9./-]*)"",\s*""([A-Z_]+)""\)|""([a-z0-9][a-z0-9./-]*/[a-z0-9./-]+|postgres|caddy|valkey)(?::([a-zA-Z0-9][a-zA-Z0-9._-]*)|@(sha256:[a-f0-9]+))""")]
    private static partial Regex CsharpImage();

    /// <summary>images.env — the source every other surface copies from.</summary>
    [GeneratedRegex(@"^([A-Z_]+)=(.+)$", RegexOptions.Multiline)]
    private static partial Regex EnvPin();

    /// <summary>A tag nobody should ship: floating, or a bare major/minor that moves under us.</summary>
    [GeneratedRegex(@"^(latest|latest-[a-z]+|[0-9]+|[0-9]+-[a-z]+)$")]
    private static partial Regex FloatingTag();

    /// <summary>Our own images, which this project builds and pushes; the kiosk pulls them by :latest on purpose.</summary>
    private static bool IsOurs(string repo) => repo.StartsWith("ghcr.io/hebelconsulting/", StringComparison.Ordinal);

    private static Dictionary<string, string> ImagesIn(string path, bool csharp, IReadOnlyDictionary<string, string> env)
    {
        var text = File.ReadAllText(path);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in (csharp ? CsharpImage() : YamlImage()).Matches(text))
        {
            string repo;
            string version;

            // `ImagePins.Image("repo", "VAR")` — resolve VAR through images.env, so what is compared is the
            // version the fixture will ACTUALLY start, not the name of the variable holding it.
            if (csharp && m.Groups[1].Success && m.Groups[2].Success)
            {
                repo = m.Groups[1].Value;
                version = env.TryGetValue(m.Groups[2].Value, out var resolved) ? resolved : string.Empty;
            }
            else
            {
                var start = csharp ? 3 : 1;
                repo = m.Groups[start].Value;
                version = Enumerable.Range(start + 1, m.Groups.Count - start - 1)
                    .Select(g => m.Groups[g])
                    .FirstOrDefault(g => g.Success)?.Value ?? string.Empty;
            }

            if (repo.Length == 0 || IsOurs(repo) || version.Length == 0)
            {
                continue;
            }

            found[repo] = version;
        }

        return found;
    }

    [Fact]
    public void Compose_the_chart_and_the_fixtures_pin_the_same_versions()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        // images.env is the SOURCE; the rest carry copies, for reasons that differ per surface and are written
        // in that file. docker-compose.yaml substitutes with an inline default so `docker compose up --build`
        // keeps working with no flags (compose auto-reads only `.env`, which is gitignored here as the local
        // override file); the chart is a literal because a Helm values.yaml cannot include another file; the
        // fixtures read images.env at startup. The copies are CHECKED rather than trusted.
        var surfaces = new (string Label, string Path, bool Csharp)[]
        {
            ("docker-compose.yaml", Path.Combine(root, "docker-compose.yaml"), false),
            ("charts/simplarchive/values.yaml", Path.Combine(root, "charts", "simplarchive", "values.yaml"), false),
            ("E2EApiFactory.cs", Path.Combine(root, "tests", "SimplArchive.EndToEndTests", "E2EApiFactory.cs"), true),
            ("SelfHostedApp.cs", Path.Combine(root, "tests", "SimplArchive.SelfHosting", "SelfHostedApp.cs"), true),
        };

        var env = EnvPin().Matches(File.ReadAllText(Path.Combine(root, "images.env")))
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim(), StringComparer.Ordinal);

        Assert.True(env.Count >= 10,
            $"images.env yielded only {env.Count} pins — it is the SOURCE, so a parse failure here would make "
            + "every comparison below vacuous.");

        var seen = surfaces.Where(s => File.Exists(s.Path))
            .Select(s => (s.Label, Images: ImagesIn(s.Path, s.Csharp, env)))
            .ToList();

        Assert.True(seen.Count == surfaces.Length,
            "A surface that names images has moved or been renamed, so this guard stopped watching it:\n"
            + string.Join("\n", surfaces.Where(s => !File.Exists(s.Path)).Select(s => $"  {s.Label}")));

        // PER SURFACE, not a total. A total lets one file go blind while the others carry the count — which is
        // exactly what happened when the fixtures moved to ImagePins and the tag-only pattern stopped matching
        // them: 18 references from compose and the chart, 0 from either fixture, and a green run.
        var blind = seen.Where(s => s.Images.Count == 0).Select(s => $"  {s.Label}").ToList();
        Assert.True(blind.Count == 0,
            "These surfaces yielded NO image references, so this guard is not checking them at all. Fix the\n"
            + "pattern rather than trusting the green run:\n" + string.Join("\n", blind));

        // 1. Nothing floats. A bare major, a bare `latest`, or `latest-full` moves under us between runs.
        var floating = seen
            .SelectMany(s => s.Images.Select(i => (s.Label, Repo: i.Key, Tag: i.Value)))
            .Where(x => FloatingTag().IsMatch(x.Tag))
            .OrderBy(x => x.Repo, StringComparer.Ordinal)
            .Select(x => $"  {x.Repo}:{x.Tag}  in {x.Label}")
            .ToList();

        Assert.True(floating.Count == 0,
            "These image references FLOAT. A floating tag in a fixture means the suite is not testing the\n"
            + "version that ships, and the build goes red on somebody else's release with no commit here:\n"
            + string.Join("\n", floating));

        // 2. Where two surfaces name the same image, they name the same version.
        var disagreements = seen
            .SelectMany(s => s.Images.Select(i => (s.Label, Repo: i.Key, Tag: i.Value)))
            .GroupBy(x => x.Repo, StringComparer.Ordinal)
            .Where(g => g.Select(x => x.Tag).Distinct(StringComparer.Ordinal).Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"  {g.Key}\n" + string.Join("\n", g.Select(x => $"      {x.Tag,-34} {x.Label}")))
            .ToList();

        Assert.True(disagreements.Count == 0,
            "These images are pinned to DIFFERENT versions in different places. A bump is one change across\n"
            + "all of them — compose, the chart and both fixtures — or the suite tests a version that does not\n"
            + "ship and the deployment runs one no test touched:\n"
            + string.Join("\n", disagreements)
            + "\n\n(The kiosk is not checked here: it lags at rollout on purpose, and"
            + "\nscripts/check-pinned-images.sh reports its drift per file.)");
    }
}
