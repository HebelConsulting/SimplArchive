namespace SimplArchive.UnitTests;

/// <summary>
/// Every shipping project's <c>.csproj</c> is copied by the Dockerfile's restore layer.
/// </summary>
/// <remarks>
/// <para>
/// The Dockerfile copies each project file individually before <c>dotnet restore</c>, so the restore layer
/// caches independently of source changes. The cost of that optimisation is a list that has to be kept in
/// step with the solution — and CLAUDE.md says so in as many words ("add a COPY line when a new project joins
/// the solution"). It was missed anyway, while adding <c>SimplArchive.Theming</c>, by someone who had read it.
/// A sentence that is followed most of the time is a guard that fires none of the time.
/// </para>
/// <para>
/// <b>What makes it worth a test is how the failure presents.</b> A missing project does not stop the restore:
/// </para>
/// <code>
/// Skipping project "SimplArchive.Theming.csproj" because it was not found.
/// </code>
/// <para>
/// Restore reports success, <c>dotnet publish</c> then fails on the unresolved reference, no image is produced,
/// and CI shows a red <b>Trivy image scan</b> — a job named after a vulnerability scanner, with nothing to say
/// about a missing file. Minutes of an image build to reach a misleading label, where this takes milliseconds
/// and names the file.
/// </para>
/// </remarks>
public class DockerfileCoversEveryProjectTests
{
    // Projects the API image genuinely does not need, each for a stated reason — an exclusion list without one
    // is where a real omission hides.
    //
    //   DesktopClient — a separate artefact, packaged by scripts/package-*.sh and never published into the
    //                   container. Listing it would make the image restore Avalonia for nothing.
    //   Worker        — Microsoft.NET.Sdk.Worker, its own host with its own deployment; the Api does not
    //                   reference it. (Surfaced by this very test on its first run, and checked rather than
    //                   assumed: `grep ProjectReference` on the Api finds nothing.)
    private static readonly string[] NotInTheImage = ["SimplArchive.DesktopClient", "SimplArchive.Worker"];

    [Fact]
    public void The_restore_layer_copies_every_project_the_image_builds()
    {
        var root = RepoPaths.Root();
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));

        var missing = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(name => !NotInTheImage.Contains(name))
            .Where(name => !dockerfile.Contains($"src/{name}/{name}.csproj", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "The Dockerfile's restore layer does not copy:\n  "
            + string.Join("\n  ", missing.Select(m => $"src/{m}/{m}.csproj"))
            + "\n\nAdd a COPY line for each. Without it `dotnet restore` inside the image SKIPS the project and "
            + "reports success; `dotnet publish` then fails on the unresolved reference, no image is built, and "
            + "CI reports it as a failing Trivy image scan.");
    }

    /// <summary>
    /// And the reverse: a line for a project that no longer exists. Harmless to the build — Docker fails loudly
    /// on a missing COPY source — but it is how the list rots, one rename at a time.
    /// </summary>
    [Fact]
    public void The_restore_layer_copies_nothing_that_has_been_removed()
    {
        var root = RepoPaths.Root();
        var present = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var stale = File.ReadAllLines(Path.Combine(root, "Dockerfile"))
            .Where(line => line.StartsWith("COPY [\"src/", StringComparison.Ordinal))
            .Select(line => line.Split('"')[1])                       // src/<name>/<name>.csproj
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .OfType<string>()
            .Where(name => !present.Contains(name))
            .ToList();

        Assert.True(
            stale.Count == 0,
            "The Dockerfile copies project files that no longer exist: " + string.Join(", ", stale));
    }

}
