using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

/// <summary>
/// The LGPL native libvips packages that are allowed for TESTS must not reach the product (issue #491).
/// </summary>
/// <remarks>
/// <para>
/// The licence gate cannot express this. It runs <c>nuget-license</c> once over the whole solution against a
/// flat list of package IDS in <c>build/licenses/ignored-packages.json</c>, with no notion of which project
/// referenced one — so the moment a test-only exception is added to that list, the same package can be added
/// to a shipping project and the gate stays green.
/// </para>
/// <para>
/// That gap opened when the exception grew from two ids to six. The product ships an Alpine image and needs
/// exactly the <c>linux-musl</c> pair; the other four exist so the test suites can run on a glibc CI runner
/// and a developer Mac without installing libvips from a distro — which shipped a version that crashed the
/// test host. Test-only was the whole basis on which the wider exception was accepted, so it is worth a guard
/// rather than a promise in a commit message.
/// </para>
/// </remarks>
public partial class TestOnlyNativePackageTests
{
    // Allowed under tests/ only. The musl pair is deliberately absent: those ARE the product's natives.
    private static readonly string[] TestOnlyNatives =
    [
        "NetVips.Native.linux-x64",
        "NetVips.Native.linux-arm64",
        "NetVips.Native.osx-arm64",
        "NetVips.Native.osx-x64",
    ];

    [GeneratedRegex(@"PackageReference\s+Include=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PackageReference();

    [Fact]
    public void No_shipping_project_references_a_test_only_native()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var project in Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            foreach (Match match in PackageReference().Matches(File.ReadAllText(project)))
            {
                var package = match.Groups[1].Value;
                if (TestOnlyNatives.Contains(package, StringComparer.OrdinalIgnoreCase))
                {
                    offenders.Add($"{Path.GetRelativePath(root, project)} references {package}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These SHIPPING projects reference an LGPL native that is only licensed here for test use — the "
            + "licence gate cannot catch it, because its ignore list is by package id and not by project "
            + "(issue #491). The product image is Alpine and needs only the linux-musl pair:\n  "
            + string.Join("\n  ", offenders));
    }

    // The other half: an id may only sit in the ignore list while something actually uses it. Otherwise the
    // exception outlives its reason and quietly licenses a package nobody chose.
    [Fact]
    public void Every_test_only_native_is_both_ignored_and_used()
    {
        var root = RepoRoot();
        var ignored = File.ReadAllText(Path.Combine(root, "build", "licenses", "ignored-packages.json"));
        var testProjects = Directory.GetFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();

        foreach (var native in TestOnlyNatives)
        {
            Assert.True(
                ignored.Contains(native, StringComparison.OrdinalIgnoreCase),
                $"{native} is expected under tests/ but is not in the licence gate's ignore list, so the gate "
                + "would fail the build on its LGPL licence.");

            Assert.True(
                testProjects.Any(p => p.Contains(native, StringComparison.OrdinalIgnoreCase)),
                $"{native} is licensed as a test-only exception but no test project references it. Remove it "
                + "from build/licenses/ignored-packages.json rather than leaving an exception without a reason.");
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SimplArchive.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
