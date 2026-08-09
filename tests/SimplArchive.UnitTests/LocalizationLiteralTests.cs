using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// User-facing text must come from SimplArchive.Localization, not from a string literal (ADRs 0468/0470/0471).
//
// LocalizationKeyTests already checks that every key a client REFERENCES exists in all four languages — it stops
// translations drifting apart. It is structurally blind to this: text that was never keyed is not a key, so it
// cannot be missing from a language. The gap that leaves is not small. Running the app in German and exercising
// check-out and external links turns up English throughout, because the app is localised where you LOOK (tabs,
// menus, labels, table headers) and English where you ACT (snackbars, confirmations, dialog titles opened from
// code). That is why it survives review: the surfaces that get looked at are the ones that were done (issue #423).
//
// This is a RATCHET, like ClientHypermediaTests. 264 literals were recorded when it was adopted (after the
// check-out flow was converted); each is recorded per file, and a file's count changing IN EITHER DIRECTION
// fails the build — adding one is a regression, and
// converting one requires lowering the budget in the same commit, so the remaining work stays a readable number
// rather than "partly localised".
//
// What it scans, and why only these three: they are the shapes that are unambiguously user-facing and mechanically
// detectable. A bare literal in C# might be a log message, a CSS class or a test fixture, so scanning for those
// would produce noise, and a guard that cries wolf gets switched off. Consequently the ledger UNDERSTATES the
// problem — MainWindowViewModel's hard-coded rights labels ("Create external links", "Override checkout") are
// real and uncounted. Lowering this to zero is necessary, not sufficient.
public partial class LocalizationLiteralTests
{
    // Snackbar.Add("…") and Snackbar.Add($"…") — the runtime feedback that is English today. The
    // interpolated form matters: it is how every "Checked out '{name}'" success message is written, and
    // an earlier version of this regex missed all of them, understating the ledger.
    [GeneratedRegex(@"Snackbar\.Add\(\s*\$?""")]
    private static partial Regex SnackbarLiteral();

    // DialogService.ShowAsync<T>("…") — a dialog title passed positionally rather than via Strings.Get.
    [GeneratedRegex(@"ShowAsync<[^>]+>\(\s*\$?""")]
    private static partial Regex DialogTitleLiteral();

    // Avalonia markup: Content/Text/ToolTip.Tip/Watermark/Title set to a literal rather than {loc:Tr …}.
    // Requires a capital-then-lowercase start so it does not match style values, brushes or enum names.
    [GeneratedRegex(@"(?:Content|Text|Header|ToolTip\.Tip|Watermark|Title)=""[A-Z][a-z][^""{]{2,}""")]
    private static partial Regex AxamlTextLiteral();

    // Seeded from the counts at adoption. LOWER an entry when you key a string; never raise one.
    private static readonly Dictionary<string, int> Budget = new()
    {
        ["src/SimplArchive.Client/Dialogs/PasskeysDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/ServiceAccountsDialog.razor"] = 14,
        ["src/SimplArchive.Client/Dialogs/VersionsDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/WorkflowDialog.razor"] = 1,
        ["src/SimplArchive.Client/Layout/MainLayout.razor"] = 1,
        ["src/SimplArchive.DesktopClient/Views/AboutDialog.axaml"] = 4,
        ["src/SimplArchive.DesktopClient/Views/AnnotationDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/BulkSensitivityDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/BulkTagsDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/ChangePasswordDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/CompareCheckoutDialog.axaml"] = 2,
        ["src/SimplArchive.DesktopClient/Views/CompareVersionsDialog.axaml"] = 2,
        ["src/SimplArchive.DesktopClient/Views/ConfirmDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/ConnectionLostDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/DropActionDialog.axaml"] = 3,
        ["src/SimplArchive.DesktopClient/Views/DuplicateUploadDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/ExportDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/ExtendRetentionDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/ExternalLinkDetailDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/FolderPickerDialog.axaml"] = 4,
        ["src/SimplArchive.DesktopClient/Views/GeneratedPasswordDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/HardDeleteAllDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/ImportOptionsDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/LegalHoldDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/LogonWindow.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/MainWindow.axaml"] = 2,
        ["src/SimplArchive.DesktopClient/Views/ManageAccessDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/MfaSetupDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/NewFolderDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/NotificationPreferencesDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/PasskeysDialog.axaml"] = 2,
        ["src/SimplArchive.DesktopClient/Views/PreviewPane.axaml"] = 13,
        ["src/SimplArchive.DesktopClient/Views/PrincipalDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/ProfilePhotoDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/ReferencesDialog.axaml"] = 3,
        ["src/SimplArchive.DesktopClient/Views/ReminderDialog.axaml"] = 3,
        ["src/SimplArchive.DesktopClient/Views/RenameDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/ReplacementReviewerDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/SendToInboxDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/SensitivityLabelsDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/ServiceAccountEditDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/ServiceAccountSecretDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/ShareSavedSearchDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/VersionsDialog.axaml"] = 2,
        ["src/SimplArchive.DesktopClient/Views/WebDavDialog.axaml"] = 1,
        ["src/SimplArchive.DesktopClient/Views/WorkflowWindow.axaml"] = 9,
    };

    [Fact]
    public void Unkeyed_user_facing_literals_do_not_exceed_the_recorded_budget()
    {
        var root = RepoRoot();
        var actual = CountByFile(root);
        var problems = new List<string>();

        foreach (var (file, count) in actual.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var budgeted = Budget.TryGetValue(file, out var b) ? b : 0;
            if (count > budgeted)
            {
                problems.Add($"  {file}: {count} unkeyed literals, budget {budgeted} — put the text in "
                    + "SimplArchive.Localization and use Strings.Get / {loc:Tr} (ADR 0471).");
            }
            else if (count < budgeted)
            {
                problems.Add($"  {file}: {count} unkeyed literals, budget {budgeted} — good news: lower the budget "
                    + $"in LocalizationLiteralTests to {count} so the ledger stays honest.");
            }
        }

        foreach (var file in Budget.Keys.Where(f => !actual.ContainsKey(f)).OrderBy(f => f, StringComparer.Ordinal))
        {
            problems.Add($"  {file}: no unkeyed literals left (or the file moved) — delete its budget entry.");
        }

        Assert.True(problems.Count == 0,
            "The localisation ledger is out of date (issue #423):\n" + string.Join("\n", problems));
    }

    // A tripwire on the headline number, so a bulk regression cannot hide behind per-file budgets.
    [Fact]
    public void The_remaining_localisation_work_is_visible()
    {
        var total = CountByFile(RepoRoot()).Values.Sum();
        Assert.True(total <= Budget.Values.Sum(),
            $"Clients carry {total} unkeyed user-facing literals, above the recorded ledger of {Budget.Values.Sum()}.");

        // Anti-vacuous: if the regexes stop matching, every assertion here passes while checking nothing.
        Assert.True(total > 50, $"expected the scan to find many literals, found {total} — it is probably broken");
    }

    private static Dictionary<string, int> CountByFile(string root)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var project in new[] { "src/SimplArchive.Client", "src/SimplArchive.DesktopClient" })
        {
            var dir = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}wwwroot{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                if (Path.GetExtension(file) is not (".cs" or ".razor" or ".axaml"))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                var n = SnackbarLiteral().Matches(text).Count
                    + DialogTitleLiteral().Matches(text).Count
                    + AxamlTextLiteral().Matches(text).Count;
                if (n > 0)
                {
                    counts[Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')] = n;
                }
            }
        }

        return counts;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
