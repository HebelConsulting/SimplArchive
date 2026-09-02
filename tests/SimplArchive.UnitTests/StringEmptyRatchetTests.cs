using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// The `string.Empty` over `""` standing principle, as a ratchet (issue #912).
//
// WHY A GUARD AND NOT JUST THE PRINCIPLE. The rule landed 2026-08-13 and by 2026-09-01 the codebase held 534
// bare `= ""` against 262 `string.Empty` — and ~82 of the violations were written AFTER the rule. A principle
// nothing measures is a preference, and this one drifted in the direction of the thing it forbids while
// reading, in CLAUDE.md, as though it were settled.
//
// WHAT IS LEFT AND WHY IT CANNOT CHANGE. `string.Empty` is a static readonly FIELD, not a constant, so the
// compiler rejects it wherever a compile-time constant is required. Every entry below is a DEFAULT PARAMETER
// in a method or record signature (CS1736). Those are the principle's own stated exception, not oversights.
//
// HOW THIS GUARD IS BUILT, AND WHY NOT BY CLASSIFYING. The obvious guard reads each line and decides whether
// it is a default parameter — and that classifier is exactly what went wrong while writing this: a hand-rolled
// census called 108 sites "attribute arguments" that were plainly field initialisers, and predicted 112
// unchangeable sites where the truth was 17. The compiler is the only reliable classifier here, and it cannot
// run inside a unit test. So this pins the COUNT PER FILE instead: a number is not a judgement, and it is
// wrong in a way anyone can check by opening the file.
//
// WHEN THIS FAILS: a count went UP, so a new bare `= ""` was written where `string.Empty` belongs — change it.
// If the new occurrence is genuinely a default parameter, raise that file's number and say so. A count that
// went DOWN is a file that paid its debt: lower it in the same commit, exactly like the 1000-line ceilings.
public class StringEmptyRatchetTests
{
    // The `= ""` occurrences the compiler REQUIRES, per file. Every one is a default parameter value.
    private static readonly Dictionary<string, int> MandatedEmptyLiterals = new()
    {
        ["src/SimplArchive.Client/Models/BrowseNode.cs"] = 6,
        // Moved from DocumentsClient to ReferencesClient with the Reference record (#518's per-area split): its
        // positional parameters carry `= ""` defaults, which CS1736 requires -- string.Empty is a static
        // readonly field, not a constant expression. Two LINES, three literals: this guard counts lines that
        // contain a match, not matches, so `string CreatedBy = "", string SensitivityLabelName = ""` scores 1.
        ["src/SimplArchive.DesktopClient/Services/ReferencesClient.cs"] = 2,
        ["src/SimplArchive.DesktopClient/Services/DragOutStager.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/IntrayApi.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/SharedRecords.cs"] = 3,
        ["src/SimplArchive.DesktopClient/Services/VersionsClient.cs"] = 1,
        ["src/SimplArchive.DesktopClient/ViewModels/PreviewPageViewModel.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Views/HighlightOverlayDrawing.cs"] = 1,
        ["src/SimplArchive.Theming/ThemeEmitter.cs"] = 1,
    };

    private static readonly Regex BareEmpty = new(@"=\s*""""(\s*[;,)\]}])", RegexOptions.Compiled);


    [Fact]
    public void No_file_gains_a_bare_empty_string_literal()
    {
        var root = RepoPaths.Root();
        var actual = new Dictionary<string, int>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var count = File.ReadLines(path).Count(l => BareEmpty.IsMatch(l));
            if (count > 0)
            {
                actual[rel] = count;
            }
        }

        var problems = new List<string>();
        foreach (var (file, count) in actual.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var allowed = MandatedEmptyLiterals.GetValueOrDefault(file, 0);
            if (count > allowed)
            {
                problems.Add($"{file}: {count} bare \"\" (allowed {allowed}) — use string.Empty, or raise the number if it is a default parameter");
            }
        }

        foreach (var (file, allowed) in MandatedEmptyLiterals.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var count = actual.GetValueOrDefault(file, 0);
            if (count < allowed)
            {
                problems.Add($"{file}: down to {count} from {allowed} — lower the number in this commit");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
