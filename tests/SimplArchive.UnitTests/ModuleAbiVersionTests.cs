using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// The ABI package's <Version> is what a module author actually consumes (ADR 0751): `abi-publish.yml` packs the
// csproj on every push to main and pushes it with `--skip-duplicate`, so a version that did NOT move is not an
// error — it is a NO-OP. That is the whole reason this guard exists: widen the ABI, forget the bump, and the
// publish job goes green having published nothing, while the module repo that needs the new surface can only
// reference the old package. Nothing fails, nothing says so, and the gap surfaces as "why can't the module see
// GetSettingAsync?" days later.
//
// It nearly happened on ADR 0772: the slice named itself ABI 0.12 in the ADR, in CLAUDE.md and in every doc
// comment on the new members, and left <Version> at 0.11.0.
//
// The anchor is the ADR filename convention every slice has followed (`…-abi-0-N-…md`), because that is where the
// version is DECIDED — the number in the csproj should be a consequence of the decision, not an independent claim
// that can drift from it.
//
// PRIVATE-REPOSITORY ONLY, like AdrIndexTests and for the same reason: `tests/` is published byte-for-byte while
// `docs/` is withheld (ADR 0484), so half this guard's input is absent in the mirror by design. The gate is the
// origin remote rather than the file's presence — "the directory isn't there" must not be indistinguishable from
// "someone deleted it".
public partial class ModuleAbiVersionTests
{
    [GeneratedRegex(@"abi-0-(\d+)-")]
    private static partial Regex AbiSliceAdr();

    [GeneratedRegex(@"<Version>(?<version>[^<]+)</Version>")]
    private static partial Regex PackageVersion();

    [Fact]
    public void The_abi_package_version_matches_the_newest_abi_slice_adr()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root || !PrivateRepositoryGate.IsPrivateRepository(root))
        {
            return; // the public mirror has no docs/, by design
        }

        var adrDir = Path.Combine(root, "docs", "adr");
        Assert.True(Directory.Exists(adrDir), "docs/adr is missing — this guard has nothing to check.");

        var slices = Directory.GetFiles(adrDir, "*.md")
            .Select(f => AbiSliceAdr().Match(Path.GetFileName(f)))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();
        Assert.True(slices.Count >= 5,
            $"Only {slices.Count} ABI-slice ADRs matched `…-abi-0-N-…md` — the naming convention changed and this "
            + "guard stopped seeing them.");

        var csprojPath = Path.Combine(root, "src", "SimplArchive.ModuleAbi", "SimplArchive.ModuleAbi.csproj");
        var version = PackageVersion().Match(File.ReadAllText(csprojPath));
        Assert.True(version.Success, $"No <Version> element in {csprojPath} — the packaging shape changed.");

        var expected = $"0.{slices.Max()}.0";
        Assert.True(version.Groups["version"].Value == expected,
            $"SimplArchive.ModuleAbi is packaged as {version.Groups["version"].Value} but the newest ABI slice ADR "
            + $"declares {expected}.\nBump <Version> in SimplArchive.ModuleAbi.csproj — `abi-publish.yml` pushes "
            + "with --skip-duplicate, so an unbumped version publishes NOTHING and reports success.");
    }
}
