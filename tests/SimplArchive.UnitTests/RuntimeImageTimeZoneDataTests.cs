namespace SimplArchive.UnitTests;

// The published image must carry the IANA time-zone database (#1138).
//
// Alpine ships no tzdata unless it is installed, and icu-libs does NOT include it — that package handles
// CULTURE, while time zones are separate data. Without it every
// TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich") throws, CalendarInstants catches it and falls back to
// the container's own zone, and every appointment naming a zone is stored at the wrong instant: an entry
// published as 10:00–22:00 Europe/Zurich was stored as 10:00Z–22:00Z instead of 08:00Z–20:00Z.
//
// It was SILENT and total. Conflict checks, availability coverage and CalDAV interop all judged instants two
// hours from where they belonged, nothing threw, and no test noticed — because every test host (a developer's
// machine, a CI runner) carries tzdata, so the suites were exercising a configuration the product never
// shipped. It took a user reading a refusal that named the wrong hours.
//
// Guarded HERE, on the Dockerfile, because that is where it broke and where a reader can check it. A test
// asserting the zone resolves would pass on every machine that runs it and prove nothing about the image.
public class RuntimeImageTimeZoneDataTests
{
    [Fact]
    public void The_runtime_image_installs_tzdata()
    {
        var dockerfile = File.ReadAllText(Path.Combine(RepositoryRoot(), "Dockerfile"));

        // The FINAL stage is the one that ships. The build and clients stages are SDK images that never do.
        var final = dockerfile[dockerfile.LastIndexOf("FROM ", StringComparison.Ordinal)..];

        // The INSTALL line, not the stage text — and comments are skipped. The first version of this test
        // asserted the word appeared anywhere in the stage, and passed with the package removed, because the
        // COMMENT above the line explaining why tzdata matters contains the word. A guard that is satisfied
        // by its own justification verifies prose, not the build.
        var installs = final
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith('#'))
            .Where(line => line.Contains("apk add", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            installs.Any(line => line.Contains("tzdata", StringComparison.Ordinal)),
            "The published image must install tzdata, or every appointment naming a time zone is stored at "
            + $"the wrong instant. Install lines found: {string.Join(" | ", installs)}");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dockerfile")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
