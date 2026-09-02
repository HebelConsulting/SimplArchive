using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// The web UI suite runs as four CI legs, split by an `Area` trait (ADR 0697). Leg 4 used to be spelled
// "everything that is not leg 1, 2 or 3", which made it a catch-all: every new test class landed there
// without anyone deciding, until it held 40 classes and ran 16.7 minutes against 11.5-12.4 for its
// siblings. Since the heavy tier's wall clock is its SLOWEST leg, that imbalance was pure waiting.
//
// These tests keep the split honest in the two ways it can rot: a class that names no leg, and a class
// that names a leg the CI matrix does not run.
//
// Parameterized over both split suites since the EndToEndTests leg was split the same way (#817): the same
// two rots apply to any trait-split suite, and a second copy of this guard is how one of them stops being
// looked at. The AREA PREFIX is per-suite ("ui-", "e2e-") so a class cannot accidentally name the OTHER
// suite's leg and vanish from its own matrix.
public class UiLegBalanceTests
{
    // Deliberately permissive about modifiers and names. The first version of this guard matched only
    // `public [sealed] class *Tests`, so it silently skipped `public partial class WebGuidedTourTests` —
    // and reported that every class named its leg while two of its tests belonged to no leg at all. A
    // guard that examines less than it claims is worse than none, because it is believed (ADR 0695).
    private static readonly Regex ClassDeclaration = new(
        @"((?:^[ \t]*\[[^\]]*\][ \t]*\r?\n)*)[ \t]*(?:public|internal)\s+(?:(?:sealed|partial|abstract|static)\s+)*class\s+(\w+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    [Theory]
    [InlineData("SimplArchive.UiEndToEndTests", "ui")]
    [InlineData("SimplArchive.EndToEndTests", "e2e")]
    public void Every_test_class_in_a_split_suite_names_the_leg_it_runs_in(string project, string prefix)
    {
        var offenders = TestClasses(project, prefix)
            .Where(c => c.Area is null)
            .Select(c => c.Name)
            .OrderBy(n => n)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These {project} test classes declare no [Trait(\"Area\", \"{prefix}-N\")], so they would all pile into "
            + $"one CI leg and slow the whole tier down. Pick the leg with the fewest cases:{Environment.NewLine}"
            + string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("SimplArchive.UiEndToEndTests", "ui")]
    [InlineData("SimplArchive.EndToEndTests", "e2e")]
    public void Every_declared_area_is_a_leg_the_ci_matrix_actually_runs(string project, string prefix)
    {
        var declared = TestClasses(project, prefix)
            .Where(c => c.Area is not null)
            .Select(c => c.Area!)
            .Distinct()
            .OrderBy(a => a)
            .ToList();

        var ci = File.ReadAllText(Path.Combine(RepoPaths.Root(), ".github", "workflows", "ci.yml"));
        var run = Regex.Matches(ci, @"Area=(" + prefix + @"-\d)").Select(m => m.Groups[1].Value).Distinct().ToList();

        // A trait nobody runs is worse than no trait: those tests silently never execute.
        var orphaned = declared.Except(run).ToList();
        Assert.True(
            orphaned.Count == 0,
            $"These areas are declared on test classes but no CI leg selects them, so those tests never run: "
            + string.Join(", ", orphaned));

        // And a leg with nothing in it is a runner spun up for nothing.
        var empty = run.Except(declared).ToList();
        Assert.True(
            empty.Count == 0,
            $"These CI legs select areas that no test class declares, so they start a runner and a container "
            + $"fleet to run nothing: {string.Join(", ", empty)}");
    }

    private static IEnumerable<(string Name, string? Area)> TestClasses(string project, string prefix)
    {
        var dir = Path.Combine(RepoPaths.Root(), "tests", project);

        // A partial class may be declared in several files with the trait on only one of them, and xUnit
        // is happy with that — so the unit is the class NAME, not the declaration.
        var areas = new Dictionary<string, string?>();
        var hasTests = new HashSet<string>();

        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var matches = ClassDeclaration.Matches(text);

            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var name = match.Groups[2].Value;

                // A class is a test class because it holds tests, not because of what it is called.
                var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
                var body = text[match.Index..end];
                if (body.Contains("[Fact") || body.Contains("[Theory"))
                {
                    hasTests.Add(name);
                }

                var area = Regex.Match(match.Groups[1].Value, @"Trait\(""Area"",\s*""(" + prefix + @"-\d)""\)");
                if (area.Success || !areas.ContainsKey(name))
                {
                    areas[name] = area.Success ? area.Groups[1].Value : null;
                }
            }
        }

        return hasTests.Select(name => (name, areas.GetValueOrDefault(name)));
    }

}
