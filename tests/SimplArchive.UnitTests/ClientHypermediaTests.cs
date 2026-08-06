using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// Anti-regression guard for the hypermedia client contract (CLAUDE.md + ADR 0543): a client must reach an
// endpoint by following a link rel the server advertised, never by composing an "api/…" URL from a template.
//
// Composed URLs are why renaming one route (issue #382 moved /comments → /chat) broke both clients at all: with a
// rel, a route move is invisible to them. The API is the sole owner of its URL space; a client that rebuilds those
// URLs has copied a private detail into two other codebases.
//
// This is a RATCHET, not a clean-slate rule. There were 381 composed URLs when the principle was adopted, and
// converting them all at once would have been an unreviewable diff across every feature in both clients. So each
// client file carries a BUDGET — the number of composed URLs it had at that moment — and this test asserts the
// real count still EQUALS it. That means:
//
//   • adding a composed URL fails the build — the count exceeds the budget, so new code cannot widen the gap;
//   • converting one to a rel ALSO fails the build, until the budget is lowered in the same commit — which keeps
//     the ledger honest and makes the remaining work a number anyone can read off, rather than a vague intention.
//
// Deleting the last composed URL in a file means deleting its entry. When every entry is gone, delete the budget
// and flip the assertion to "no client composes an api/ URL" — that is the finish line.
public class ClientHypermediaTests
{
    // Matches a string literal that starts an API path: "api/…" or $"api/…". Deliberately narrow — it catches the
    // way both clients actually build these, without flagging prose in a comment.
    private static readonly Regex ComposedApiUrl = new("\\$?\"api/", RegexOptions.Compiled);

    // file (repo-relative, forward slashes) → composed "api/…" literals it still contains.
    //
    // Seeded from the counts at adoption. LOWER an entry when converting a call site; never raise one.
    private static readonly Dictionary<string, int> Budget = new()
    {
        ["src/SimplArchive.Client/Dialogs/BulkTagsDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/ChangePasswordDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/CompareCheckoutDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/CompareVersionsDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/FilingDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/FolderPickerDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/InboxSendDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/ManageAccessDialog.razor"] = 7,
        ["src/SimplArchive.Client/Dialogs/MfaSetupDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/NotificationPreferencesDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/PasskeysDialog.razor"] = 4,
        ["src/SimplArchive.Client/Dialogs/ProfilePhotoDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/ReferencesDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/ReminderDialog.razor"] = 4,
        ["src/SimplArchive.Client/Dialogs/SensitivityLabelsDialog.razor"] = 5,
        ["src/SimplArchive.Client/Dialogs/ServiceAccountsDialog.razor"] = 5,
        ["src/SimplArchive.Client/Dialogs/VersionsDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/WebDavDialog.razor"] = 3,
        ["src/SimplArchive.Client/Dialogs/WorkflowDialog.razor"] = 2,
        ["src/SimplArchive.Client/Layout/MainLayout.razor"] = 7,
        ["src/SimplArchive.Client/Pages/Home.razor"] = 141,
        ["src/SimplArchive.DesktopClient/Services/SimplArchiveApiClient.cs"] = 184,
    };

    [Fact]
    public void Clients_do_not_compose_api_urls_beyond_the_recorded_budget()
    {
        var actual = CountByFile();

        var regressions = new List<string>();

        foreach (var (file, count) in actual.OrderBy(kv => kv.Key))
        {
            var budgeted = Budget.TryGetValue(file, out var b) ? b : 0;
            if (count > budgeted)
            {
                regressions.Add($"  {file}: {count} composed api/ URLs, budget {budgeted} — follow a link rel "
                    + "from the resource instead of composing the URL (ADR 0543).");
            }
            else if (count < budgeted)
            {
                regressions.Add($"  {file}: {count} composed api/ URLs, budget {budgeted} — good news: lower the "
                    + "budget in ClientHypermediaTests to {count} so the ledger stays honest.".Replace("{count}", count.ToString()));
            }
        }

        foreach (var file in Budget.Keys.Where(f => !actual.ContainsKey(f)).OrderBy(f => f))
        {
            regressions.Add($"  {file}: no composed api/ URLs left (or the file moved) — delete its budget entry.");
        }

        Assert.True(regressions.Count == 0,
            "The client hypermedia ledger is out of date (ADR 0543):\n" + string.Join("\n", regressions));
    }

    // The API owns its URL space, so no client should be inventing routes the server never advertised. This is the
    // finish line the budget is walking towards; it is informational until the budget empties.
    [Fact]
    public void The_remaining_conversion_work_is_visible()
    {
        var total = CountByFile().Values.Sum();

        // Purely a tripwire on the headline number, so a bulk regression can't hide behind per-file budgets.
        Assert.True(total <= Budget.Values.Sum(),
            $"Clients compose {total} api/ URLs in total, above the recorded ledger of {Budget.Values.Sum()} (ADR 0543).");
    }

    private static Dictionary<string, int> CountByFile()
    {
        var root = RepoRoot();
        var counts = new Dictionary<string, int>();

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

                var matches = ComposedApiUrl.Matches(File.ReadAllText(file)).Count;
                if (matches > 0)
                {
                    counts[Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')] = matches;
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

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root (SimplArchive.slnx).");
    }
}
