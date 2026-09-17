using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// The core-only DAV kind table is read in ONE place; everywhere else asks the registry (#1261, ADR 0803).
//
// WHY A GUARD RATHER THAN A THIRD FIX. `DavCollectionKinds.All` is the CORE kinds only, so any site reading it
// directly is blind to every module-declared kind (ADR 0791) by construction. That mistake has now been made
// three times, and each fix moved it one level up instead of ending it:
//
//   1. `DavProtocol` carried a hand-written list of masks — fixed by deriving from the table.
//   2. `ChildCreationPolicy.AdmitsCalendarEntries` derived from the table, and still missed Maintenance
//      (ADR 0778) and Availability (ADR 0780) — so an Availability folder advertised no create ANYWHERE.
//   3. The same predicate again (#1242): it derived from the CORE-ONLY table, so a module's Logbook was
//      advertised without the one rel its entries live at — listed in the Calendar tab, tickable, and
//      permanently empty, while CalDAV served it perfectly.
//
// Each of those was invisible until somebody used the feature, because the symptom is an ABSENCE: no rel, no
// create, no entries. Nothing throws, nothing logs, and the screen simply shows less than it should. A guard is
// the only thing that turns "silently missing" into "the build says so".
public class DavKindsAreReadThroughTheRegistryTests
{
    // Where the core table may legitimately be read, each with the reason. An entry here is a decision somebody
    // made — not a way to make this test quiet — and the second test below fails if one stops being needed.
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        // NOT the table's own file: inside it the property is `All`, unqualified, so the pattern never matches
        // there. Listing it looked obviously right and was caught by the staleness test on the first run —
        // which is the staleness test earning its place before the guard had shipped.

        ["src/SimplArchive.Infrastructure/Modules/DavCollectionKindRegistry.cs"] =
            "THE registry — it seeds itself from the core set and adds each module's kinds",

        ["src/SimplArchive.Infrastructure/Persistence/SimplArchiveDbContext.BookableCollections.cs"] =
            "CORE-ONLY ON PURPOSE (#1261): this provisions a RESOURCE's own collections when it becomes "
            + "bookable. A module provisions its own where it wants them — the flight school's Logbooks sit "
            + "under aircraft AND pilot dossiers rather than uniformly on every bookable resource — so "
            + "deriving this from the registry would auto-create a module's read-only collection on every "
            + "bookable resource, duplicating what the module already seeds. Measured before concluding.",

        ["src/SimplArchive.Infrastructure/Persistence/SimplArchiveDbContext.cs"] =
            "the fallback when no registry is injected — reachable only from DatabaseMigrator and the "
            + "design-time factory, neither of which writes a document. Verified live: DavCollectionChanges "
            + "carries rows for module collections, so the injected path is what runs (#1261).",
    };

    [Fact]
    public void Only_the_named_files_read_the_core_only_kind_table()
    {
        var offenders = SourceFiles()
            .Where(file => Reads(file.Text))
            .Select(file => file.RelativePath)
            .Where(path => !Allowed.ContainsKey(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These files read `DavCollectionKinds.All`, which is the CORE kinds only — so they are blind to "
            + "every module-declared kind (ADR 0791) by construction:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nAsk `IDavCollectionKindRegistry` instead. If the core-only set is genuinely what this site "
            + "wants, add it to `Allowed` above WITH THE REASON — and measure that it is right before you do, "
            + "because the failure mode here is an ABSENCE nobody is told about.");
    }

    [Fact]
    public void The_allowlist_names_only_files_that_still_read_it()
    {
        // The direction an allowlist always rots in: an entry that stopped being needed silently widens the
        // rule, and nothing ever points at it. Same shape as the workaround-dependency rule — an exception
        // outlives its reason unless something checks.
        var reading = SourceFiles().Where(file => Reads(file.Text)).Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        var stale = Allowed.Keys.Where(path => !reading.Contains(path)).OrderBy(p => p, StringComparer.Ordinal).ToList();

        Assert.True(stale.Count == 0,
            "These allowlist entries name files that no longer read the core table — remove them, or the "
            + "exception outlives the reason it was granted for:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void The_scan_actually_finds_something()
    {
        // Anti-vacuous. A path typo or a moved directory would make both tests above pass by scanning nothing,
        // which is the quietest way for a guard to stop guarding.
        Assert.True(SourceFiles().Count() > 200, "the source scan found almost no files — check the root path");
        Assert.Contains(SourceFiles(), file => Reads(file.Text));
    }

    // `DavCollectionKinds.All` — the property, not a mention in prose. Comment lines are skipped: adding this
    // guard instantly flagged the comments EXPLAINING it, and a guard that punishes documenting the rule
    // teaches people to stop documenting it (the same calibration the rel-scanner needed, #862).
    private static bool Reads(string text) =>
        text.Split('\n')
            .Select(line => line.TrimStart())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal) && !line.StartsWith("///", StringComparison.Ordinal)
                && !line.StartsWith("*", StringComparison.Ordinal))
            .Any(line => KindsAll().IsMatch(line));

    private static Regex KindsAll() => new(@"\bDavCollectionKinds\s*\.\s*All\b", RegexOptions.Compiled);

    private static IEnumerable<(string RelativePath, string Text)> SourceFiles()
    {
        var root = RepositoryRoot();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            // Build output, and EF's generated migration snapshots — neither is authored.
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(path));
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
