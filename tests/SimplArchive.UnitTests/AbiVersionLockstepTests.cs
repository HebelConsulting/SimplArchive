using System.Text.RegularExpressions;
using SimplArchive.ModuleAbi;

namespace SimplArchive.UnitTests;

// The ABI version exists in TWO places that must agree (#1306).
//
// `SimplArchive.ModuleAbi.csproj`'s `<Version>` is what the PACKAGE says it is — what a module author sees and
// builds against. `ModuleAbiVersion.Minor` is what the HOST enforces:
//
//     public static bool MinorCompatible(int moduleAbiMinor) => moduleAbiMinor <= ModuleAbiVersion.Minor;
//
// Nothing checked them against each other, and they drifted. ADR 0804 ("ABI 0.26") added 33 lines of ABI
// surface to `ModuleMaskSeed` and bumped the csproj to 0.26.0 — commit 5f9a69df — and never touched
// `ModuleAbiVersion.cs`, which still said 25.
//
// WHAT THAT COSTS, and why it is worse than it looks. A module built against the published 0.26 package that
// honestly declares `AbiMinorVersion = 26` is REFUSED: logged at Warning, dropped from the loaded set. It does
// not crash — #1147's fix is doing its job — it silently is not there, and its routes answer
// `404 MODULE_NOT_ACTIVE`, which reads as "the feature was never built" rather than "the module was refused"
// (the confusion #1242 is about). The bug therefore punishes the module author who tells the truth about what
// they compiled against, and rewards the one who leaves the declaration stale.
//
// It never fired only because no module had yet claimed 26.
public partial class AbiVersionLockstepTests
{
    [GeneratedRegex(@"<Version>(\d+)\.(\d+)\.(\d+)</Version>")]
    private static partial Regex PackageVersion();

    [Fact]
    public void The_host_enforces_the_version_the_package_advertises()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "SimplArchive.ModuleAbi", "SimplArchive.ModuleAbi.csproj"));
        var match = PackageVersion().Match(csproj);
        Assert.True(match.Success, "SimplArchive.ModuleAbi.csproj has no <Version> — this guard has nothing to compare.");

        var packageMajor = int.Parse(match.Groups[1].Value);
        var packageMinor = int.Parse(match.Groups[2].Value);

        Assert.True(packageMajor == ModuleAbiVersion.Major && packageMinor == ModuleAbiVersion.Minor,
            $"The ABI package advertises {packageMajor}.{packageMinor} while the host enforces "
            + $"{ModuleAbiVersion.Major}.{ModuleAbiVersion.Minor}.\n\n"
            + "These are two copies of one fact and they have drifted before (#1306). The direction that bites: "
            + "when the PACKAGE is ahead, a module built against it and honestly declaring its minor is refused "
            + "by MinorCompatible — silently dropped, with its routes answering 404 MODULE_NOT_ACTIVE as though "
            + "the feature had never been written.\n\n"
            + "Bump ModuleAbiVersion (src/SimplArchive.ModuleAbi/ModuleAbiVersion.cs) and the csproj <Version> "
            + "together. An ABI ADR that changes the surface must move both.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
