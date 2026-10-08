using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// modules.env is the SOURCE of every industry-module pin; the chart carries a copy, and the copies are CHECKED
// rather than trusted (ADR 0799).
//
// WHY A COPY EXISTS AT ALL. A Helm values.yaml cannot include another file and a chart cannot read outside
// itself, so the chart must restate both the pins and the installer script. That is the same arrangement
// images.env already has with the image tags — and the same hazard.
//
// THE HAZARD IS NOT HYPOTHETICAL, and it is why this test was written the day the second copy was created:
// the chart's OTHER file copy, `files/db-init.sql`, was never guarded. ADR 0721 added the static
// `simplarchive_runtime` login to `scripts/db-init.sql` and the chart copy never got it — 6 mentions on one
// side, 0 on the other, drifted in BOTH directions, and nothing failed (#1249). A copy nobody compares is how
// a deployment shape quietly stops installing something.
public partial class ModulePinLockstepTests
{
    [GeneratedRegex(@"^([A-Z0-9_]+)_(PACKAGE|VERSION|SHA512)=(.*)$", RegexOptions.Multiline)]
    private static partial Regex EnvPin();

    [Fact]
    public void The_chart_pins_the_same_module_versions_as_modules_env()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var pins = Pins(File.ReadAllText(Path.Combine(root, "modules.env")));

        // Anti-vacuous. Every assertion below is driven by this parse, so a parse that finds nothing would make
        // the whole test pass while comparing nothing — the exact failure this file exists to prevent.
        Assert.True(pins.Count >= 1,
            "modules.env yielded no pins. It is the SOURCE, so a parse failure here makes every comparison "
            + "below vacuous — fix the parse or the file, never this assertion.");

        var chart = File.ReadAllText(Path.Combine(root, "charts", "simplarchive", "values.yaml"));

        var drifted = pins
            .Where(p => !ChartDeclares(chart, p.Key, p.Value))
            .Select(p => $"  {p.Key}: modules.env says package={p.Value.Package} version={p.Value.Version} "
                       + $"sha512={Shorten(p.Value.Sha512)} — the chart's modules.pins does not say the same")
            .ToList();

        Assert.True(drifted.Count == 0,
            "charts/simplarchive/values.yaml disagrees with modules.env:\n"
            + string.Join("\n", drifted)
            + "\n\nmodules.env is the source. A chart that pins a different version installs software nobody "
            + "asked for, and a chart that pins a different digest refuses to install at all.");
    }

    [Fact]
    public void A_pinned_version_carries_a_digest()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        // 0.0.0 is the deliberate placeholder for a module whose repository has not published yet. It is
        // allowed to carry no digest precisely BECAUSE nothing may install it: modules-init refuses a 0.0.0
        // pin outright, so the placeholder cannot reach a deployment.
        var naked = Pins(File.ReadAllText(Path.Combine(root, "modules.env")))
            .Where(p => p.Value.Version != "0.0.0" && string.IsNullOrWhiteSpace(p.Value.Sha512))
            .Select(p => $"  {p.Key} is pinned at {p.Value.Version} with no {p.Key}_SHA512")
            .ToList();

        Assert.True(naked.Count == 0,
            "These modules pin a version but no digest:\n" + string.Join("\n", naked)
            + "\n\nA version without a digest installs bytes nobody has checked, which is the thing ADR 0799 "
            + "exists to end. Add the digest (the base64 NuGet records in <id>.<version>.nupkg.sha512).");
    }

    [GeneratedRegex(@"SA_MODULE_FEED:-([^}""]+)\}")]
    private static partial Regex FeedDefault();

    [GeneratedRegex(@"^modules:\s*\n(?:[ \t]+.*\n|[ \t]*\n)*?[ \t]+feed:\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex ChartFeed();

    // WHERE modules come from is one value in five places (core #1555). The day the vendor feed becomes the source,
    // every default moves at once — and the rollout order is strict: the feed must hold every pinned version
    // BEFORE any installer points at it, because an empty feed stops every instance from starting (ADR 0799). A
    // file left behind on the old feed is a deployment quietly installing from somewhere else, and one moved early
    // is a deployment that cannot start. So they are compared, not trusted.
    //
    // The two tools/ stacks are private-repository-only: tools/ is withheld from the public mirror (ADR 0484).
    [Fact]
    public void Every_installer_defaults_to_the_same_module_feed()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var files = new List<string> { "scripts/modules-init.sh", "docker-compose.yaml" };
        if (PrivateRepositoryGate.IsPrivateRepository(root))
        {
            // Not tools/vendor: that stack IS the vendor feed and installs from GitHub Packages, because its modules-init
            // runs before its own api is up (#1656). The kiosk is a customer like any other.
            files.Add("tools/kiosk/docker-compose.yml");
        }

        var defaults = files.ToDictionary(f => f, f => FeedDefault().Matches(File.ReadAllText(Path.Combine(root, f)))
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList());

        var chart = ChartFeed().Match(File.ReadAllText(Path.Combine(root, "charts", "simplarchive", "values.yaml")));
        Assert.True(chart.Success, "charts/simplarchive/values.yaml has no modules.feed — this guard cannot read it.");
        defaults["charts/simplarchive/values.yaml (modules.feed)"] = [chart.Groups[1].Value];

        var silent = defaults.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList();
        Assert.True(silent.Count == 0,
            $"No SA_MODULE_FEED default found in: {string.Join(", ", silent)}. The shape changed and this guard stopped seeing it.");

        var distinct = defaults.Values.SelectMany(v => v).Distinct(StringComparer.Ordinal).ToList();
        Assert.True(distinct.Count == 1,
            "The installers default to different module feeds:\n"
            + string.Join("\n", defaults.Select(kv => $"  {kv.Key}: {string.Join(" | ", kv.Value)}"))
            + "\n\nMove them in ONE change, and only once the new feed holds every pinned version (core #1555).");
    }

    [Fact]
    public void The_charts_copy_of_the_installer_is_byte_identical()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var source = Path.Combine(root, "scripts", "modules-init.sh");
        var copy = Path.Combine(root, "charts", "simplarchive", "files", "modules-init.sh");

        Assert.True(File.Exists(copy),
            $"{copy} is missing. The chart renders it into a ConfigMap and cannot read outside itself, so the "
            + "copy is required — and it is required to be identical, which is what the rest of this asserts.");

        Assert.True(File.ReadAllText(source) == File.ReadAllText(copy),
            "scripts/modules-init.sh and charts/simplarchive/files/modules-init.sh have diverged.\n\n"
            + "Copy the script over the chart's copy; do not edit one of them. Two installers that differ mean "
            + "compose and Kubernetes install modules differently, and only one of them was tested (#1249 is "
            + "what that looks like after a year).");
    }

    private static Dictionary<string, (string Package, string Version, string Sha512)> Pins(string env)
    {
        var fields = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (Match m in EnvPin().Matches(env))
        {
            if (!fields.TryGetValue(m.Groups[1].Value, out var f))
            {
                fields[m.Groups[1].Value] = f = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            f[m.Groups[2].Value] = m.Groups[3].Value.Trim();
        }

        return fields
            .Where(kv => kv.Value.ContainsKey("PACKAGE"))
            .ToDictionary(
                kv => kv.Key,
                kv => (kv.Value["PACKAGE"],
                       kv.Value.GetValueOrDefault("VERSION", string.Empty),
                       kv.Value.GetValueOrDefault("SHA512", string.Empty)),
                StringComparer.Ordinal);
    }

    // The chart nests its pins under `modules.pins.<KEY>`, so the check is that the key's block names the same
    // package, version and digest — not that the file happens to contain the strings somewhere.
    private static bool ChartDeclares(string chart, string key, (string Package, string Version, string Sha512) pin)
    {
        var block = Regex.Match(chart, $@"^\s+{Regex.Escape(key)}:\s*$(?<body>(\n\s+\S.*)+)", RegexOptions.Multiline);
        if (!block.Success)
        {
            return false;
        }

        var body = block.Groups["body"].Value;
        return Regex.IsMatch(body, $@"package:\s*{Regex.Escape(pin.Package)}\s*$", RegexOptions.Multiline)
            && Regex.IsMatch(body, $@"version:\s*""?{Regex.Escape(pin.Version)}""?\s*$", RegexOptions.Multiline)
            && Regex.IsMatch(body, $@"sha512:\s*""?{Regex.Escape(pin.Sha512)}""?\s*$", RegexOptions.Multiline);
    }

    private static string Shorten(string digest) =>
        digest.Length <= 12 ? $"'{digest}'" : $"'{digest[..12]}…'";
}
