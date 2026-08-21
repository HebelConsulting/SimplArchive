using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// Every [EditorRequired] parameter on a dialog is actually supplied by every caller that launches it.
//
// WHY THIS EXISTS RATHER THAN THE ATTRIBUTE ALONE. `[EditorRequired]` is a Razor MARKUP diagnostic (RZ2012):
// the compiler raises it when a component is written as a tag without the parameter. Dialogs are never written
// as tags — they are launched through `DialogService.ShowAsync<T>(…)` with a runtime `DialogParameters`
// dictionary, which the Razor compiler never looks inside. So on this call shape the attribute compiles clean
// no matter what is omitted, and treating it as enforcement is a guard that only sees the shape its author
// imagined.
//
// It cost a real bug. `VersionsDialog` launched `CompareVersionsDialog` with DocumentId and DocumentName but
// no VersionsHref; the dialog's load begins `if (VersionsHref is null) { return; }`, so it opened with no
// versions and both pickers — bound to `Guid` — rendering the default as "00000000-0000-0000-0000-000000000000".
// Reported from the live demo as "version comparison is broken, seems with null guid". The detail pane's own
// Compare button passed the href and worked, which is exactly why one broken caller survived: the feature was
// demonstrably fine by the route anyone testing it would take.
//
// So the attribute is the SPEC and this test is the ENFORCEMENT: mark a dialog parameter [EditorRequired] and
// every launcher must name it.
//
// SCOPE, stated plainly: matching is per FILE, not per call. A file that launches the same dialog twice and
// supplies the parameter only once passes. That is deliberate — locating "the dictionary belonging to this
// call" by text is guesswork, and a guard that is subtly wrong is worse than one that is bluntly right. The
// case it does catch is the one that happened: a launcher that never mentions the parameter at all.
public class EditorRequiredDialogParameterTests
{
    [Fact]
    public void Every_caller_supplies_every_editor_required_dialog_parameter()
    {
        var root = RepoRoot();
        var client = Path.Combine(root, "src", "SimplArchive.Client");

        var required = RequiredParametersByDialog(client);
        Assert.NotEmpty(required); // anti-vacuous: no [EditorRequired] found means the scan, not the code, changed

        var sources = SourceFiles(client).ToList();
        var launches = 0;
        var offenders = new List<string>();

        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            foreach (var (dialog, parameters) in required)
            {
                // `ShowAsync<CompareVersionsDialog>` and `ShowAsync<Dialogs.CompareVersionsDialog>` both count.
                if (!Regex.IsMatch(text, $@"ShowAsync<\s*(?:[\w.]+\.)?{Regex.Escape(dialog)}\s*>"))
                {
                    continue;
                }

                launches++;
                foreach (var parameter in parameters.Where(p => !text.Contains($"\"{p}\"", StringComparison.Ordinal)))
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)} launches {dialog} without [\"{parameter}\"]");
                }
            }
        }

        Assert.True(launches > 0, "no dialog launches were found at all — the ShowAsync pattern must have changed");
        Assert.True(
            offenders.Count == 0,
            "A dialog is launched without a parameter it declares [EditorRequired]. The Razor compiler cannot see "
            + "this, because DialogParameters is a runtime dictionary — supply the parameter at the call site.\n"
            + string.Join("\n", offenders));
    }

    // The specific case that motivated the rule, pinned on its own so the general test above cannot go quiet
    // about it by some scan detail drifting.
    [Fact]
    public void The_versions_dialog_hands_the_compare_dialog_its_version_list_address()
    {
        var versionsDialog = Path.Combine(RepoRoot(), "src", "SimplArchive.Client", "Dialogs", "VersionsDialog.razor");
        var text = File.ReadAllText(versionsDialog);

        Assert.Contains("ShowAsync<CompareVersionsDialog>", text, StringComparison.Ordinal);
        Assert.Contains("\"VersionsHref\"", text, StringComparison.Ordinal);
    }

    private static Dictionary<string, List<string>> RequiredParametersByDialog(string clientRoot)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in SourceFiles(clientRoot).Where(f => f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)))
        {
            // `[Parameter, EditorRequired]` and `[Parameter][EditorRequired]`, in either order, then the
            // declaration whose LAST identifier before `{ get;` is the parameter name.
            var matches = Regex.Matches(
                File.ReadAllText(file),
                @"\[\s*(?:Parameter\s*,\s*EditorRequired|EditorRequired\s*,\s*Parameter)\s*\]\s*public\s+[^;{]*?(\w+)\s*\{\s*get;");

            if (matches.Count > 0)
            {
                result[Path.GetFileNameWithoutExtension(file)] = matches.Select(m => m.Groups[1].Value).ToList();
            }
        }

        return result;
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root (SimplArchive.slnx).");
    }
}
