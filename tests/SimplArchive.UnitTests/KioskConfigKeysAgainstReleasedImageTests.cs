using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// The kiosk's config comes from `main`; its IMAGE comes from the last `v*` tag. So a configuration key renamed
// since that tag is delivered to a binary that has never heard of it — and the old binary cannot refuse what it
// cannot recognise (#1382).
//
// The asymmetry is the whole problem, and only one half is loud: a NEW binary meeting a RETIRED key refuses to
// start and names the replacement (`EncryptionModes.ThrowIfLegacyConfigured`), which works. An OLD binary
// meeting a NEW key simply IGNORES it and falls back to whatever the absent old key means. On 2026-09-25 that
// fallback switched at-rest encryption ON for the public Demo tenant — `Encryption:Tenants` absent means every
// tenant, deliberately — and the symptom surfaced hours later as the desktop client failing to preview every
// rendition written since (#1381, #1375).
//
// So this guard asks the one question CI can answer: does the RELEASED code contain the section every kiosk
// config key names? Calibrated before being written, which is the only reason it is here rather than in the
// "rejected" list of ADR 0836: measured against v0.33.0 it flags **0 of 39** keys, and measured against
// v0.32.0 — the image that was live when the demo broke — it flags exactly the one that broke it.
public partial class KioskConfigKeysAgainstReleasedImageTests
{
    // `Encryption__Modes__Crypto:` at the start of a line, indented, in a compose environment block.
    [GeneratedRegex(@"^\s+([A-Za-z][A-Za-z0-9]*(?:__[A-Za-z0-9]+)+):", RegexOptions.Multiline)]
    private static partial Regex ComposeEnvironmentKey();

    [Fact]
    public void Every_kiosk_config_key_names_a_section_the_released_image_understands()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root || !PrivateRepositoryGate.IsPrivateRepository(root))
        {
            return; // tools/ is withheld from the public mirror (ADR 0484)
        }

        var kiosk = Path.Combine(root, "tools", "kiosk");
        Assert.True(Directory.Exists(kiosk), $"{kiosk} is missing — this guard has nothing to read.");

        // FAILS rather than skips when there is no tag to compare against, and says how to fix it. A guard that
        // stands down on a shallow CI clone is the shape that let three releases ship with no .dmg: it reports
        // success having checked nothing, and the report is indistinguishable from a real pass.
        var tag = LastReleaseTag(root);
        Assert.False(string.IsNullOrEmpty(tag),
            "No v* tag is visible, so this guard cannot compare the kiosk's config against the released image. "
            + "A default actions/checkout fetches no tags — add `with: { fetch-tags: true }` to the job that runs "
            + "the unit tests.");

        // A SENTINEL, so this guard can never confuse "the clone does not have the tag's tree" with "the key was
        // renamed". Without it a shallow clone that carries the tag REF but not its objects would flag every key
        // at once and blame the rename — loud, and pointing at the wrong thing, which costs more than silence.
        Assert.True(ReleasedCodeMentions(root, tag!, "namespace SimplArchive"),
            $"The tag {tag} is visible but its tree is not readable in this clone, so nothing can be compared "
            + "against it. Add `with: { fetch-tags: true }` to the checkout (or fetch that tag explicitly) rather "
            + "than trusting this guard's verdict.");

        var keys = Directory.EnumerateFiles(kiosk, "*.yml")
            .SelectMany(f => ComposeEnvironmentKey().Matches(File.ReadAllText(f))
                .Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(keys.Count >= 20,
            $"Only {keys.Count} config keys matched in tools/kiosk/*.yml — the compose shape changed and this "
            + "guard stopped seeing them.");

        var unknown = keys
            .Select(key => (Key: key, Section: SectionOf(key)))
            .Where(x => x.Section is not null && !ReleasedCodeMentions(root, tag!, $"\"{x.Section}"))
            .Select(x => $"  {x.Key}  (section '{x.Section}')")
            .ToList();

        Assert.True(unknown.Count == 0,
            $"The kiosk bundle sets config keys that {tag} does not understand:\n{string.Join("\n", unknown)}\n\n"
            + "The kiosk syncs config from main but runs the last RELEASED image, so such a key is silently "
            + "IGNORED on the host — and the fallback can flip a security-relevant default in either direction "
            + "without erroring (#1382). Keep the released spelling in the bundle and flip it in the release "
            + "change, so config and image move in one rollout (docs/deploy/release.md).");
    }

    /// <summary>The configuration SECTION a double-underscore key names — everything but its last segment.</summary>
    /// <remarks>
    /// The section rather than the whole key, because the leaf is usually a value name the code never spells as a
    /// literal (it binds a POCO, or composes the leaf from a tenant name — <c>Encryption:Modes:&lt;tenant&gt;</c>
    /// is exactly that). The section is what a renamed key changes and what the code does spell.
    /// </remarks>
    private static string? SectionOf(string doubleUnderscoreKey)
    {
        var segments = doubleUnderscoreKey.Split("__");
        return segments.Length < 2 ? null : string.Join(':', segments[..^1]);
    }

    /// <summary>Whether the released tree spells this section as a string literal anywhere under <c>src/</c>.</summary>
    private static bool ReleasedCodeMentions(string root, string tag, string text) =>
        // Searched VERBATIM. Callers pass the quote themselves where they mean a string literal — a section name
        // is only a configuration key when it is quoted, which is what separates it from prose in a comment that
        // happens to name the same thing.
        Git(root, "grep", "--quiet", "--fixed-strings", text, tag, "--", "src").ExitCode == 0;

    private static string? LastReleaseTag(string root)
    {
        var result = Git(root, "tag", "--list", "v*", "--sort=-v:refname");
        return result.ExitCode != 0
            ? null
            : result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
    }

    private static (int ExitCode, string Output) Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return (-1, string.Empty);
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
