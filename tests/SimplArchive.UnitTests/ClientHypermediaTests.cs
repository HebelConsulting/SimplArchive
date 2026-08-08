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
public partial class ClientHypermediaTests
{
    // Matches a string literal that starts an API path: "api/…" or $"api/…". Deliberately narrow — it catches the
    // way both clients actually build these, without flagging prose in a comment.
    private static readonly Regex ComposedApiUrl = new("\\$?\"api/", RegexOptions.Compiled);

    // file (repo-relative, forward slashes) → composed "api/…" literals it still contains.
    //
    // Seeded from the counts at adoption. LOWER an entry when converting a call site; never raise one.
    private static readonly Dictionary<string, int> Budget = new()
    {
                ["src/SimplArchive.Client/Dialogs/ChangePasswordDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/CompareCheckoutDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/CompareVersionsDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/FilingDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/FolderPickerDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/InboxSendDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/ManageAccessDialog.razor"] = 7,
        ["src/SimplArchive.Client/Dialogs/MfaSetupDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/NotificationPreferencesDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/PasskeysDialog.razor"] = 4,
        ["src/SimplArchive.Client/Dialogs/ProfilePhotoDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/ReferencesDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/ReminderDialog.razor"] = 4,
        ["src/SimplArchive.Client/Dialogs/SensitivityLabelsDialog.razor"] = 3,
        ["src/SimplArchive.Client/Dialogs/ServiceAccountsDialog.razor"] = 3,
        ["src/SimplArchive.Client/Dialogs/VersionsDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/WebDavDialog.razor"] = 3,
        ["src/SimplArchive.Client/Dialogs/WorkflowDialog.razor"] = 2,
        ["src/SimplArchive.Client/Layout/MainLayout.razor"] = 7,
        ["src/SimplArchive.Client/Pages/Home.razor"] = 110,
        // 184 → 183 (issue #385): the desktop read the document resource TWICE — once for its name, once for its
        // sensitivity label — so the per-document external-links rel had nowhere to be picked up from. One read
        // now serves both and carries the rel, which is what let the dialog follow it instead of composing a URL.
        //
        // 183 → 153 (issue #416, tranche A): the 19 top-level COLLECTION roots now come from the API root's own
        // rels via the cached RootHrefAsync. What remains here is overwhelmingly the interpolated kind
        // ($"api/documents/{id}/…"), which needs a resource in hand rather than a path — the structural half of
        // the burn-down, and a separate piece of work.
        ["src/SimplArchive.DesktopClient/Services/SimplArchiveApiClient.cs"] = 153,
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

    // Every root rel a client demands must actually be advertised by RootController.
    //
    // Converting a composed URL to a rel that does not exist swaps a working call for an exception, and the
    // exception happens at RUNTIME, in whichever screen happens to use it — so it shows up as one unrelated test
    // failing, or not at all if nothing exercises that path. That is exactly what happened while converting
    // tranche A of issue #416: nine of the ten needed rels were added, `tags` was missed, and the only signal was
    // two desktop tag tests failing several minutes later. This turns that into a build error naming the rel.
    [Fact]
    public void Every_root_rel_the_clients_follow_is_advertised_by_the_api()
    {
        var root = RepoRoot();
        var demanded = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in ClientFiles(root))
        {
            foreach (Match m in RootRelUse().Matches(File.ReadAllText(file)))
            {
                demanded.Add(m.Groups["rel"].Value);
            }
        }

        var controller = File.ReadAllText(Path.Combine(root, "src", "SimplArchive.Api", "Controllers", "RootController.cs"));
        var advertised = AdvertisedRel().Matches(controller).Select(m => m.Groups["rel"].Value).ToHashSet(StringComparer.Ordinal);

        var missing = demanded.Where(r => !advertised.Contains(r)).ToList();

        Assert.True(missing.Count == 0,
            "These root rels are followed by a client but not advertised by RootController — following one throws "
            + "at runtime (ADR 0543):\n  " + string.Join("\n  ", missing));

        // Anti-vacuous: if the regexes stop matching, the assertion above passes while checking nothing.
        Assert.True(demanded.Count > 10, $"expected the clients to follow many root rels, found {demanded.Count} — the scan is probably broken");
    }

    [GeneratedRegex(@"(?:RequireAsync|HrefAsync|RootHrefAsync)\(""(?<rel>[A-Za-z]+)""")]
    private static partial Regex RootRelUse();

    [GeneratedRegex(@"new Link\(""(?<rel>[A-Za-z]+)""")]
    private static partial Regex AdvertisedRel();

    // One definition of "a client source file", shared by both tests — so the rel guard and the ledger can never
    // disagree about what they are scanning.
    private static IEnumerable<string> ClientFiles(string root)
    {
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

                yield return file;
            }
        }
    }

    private static Dictionary<string, int> CountByFile()
    {
        var root = RepoRoot();
        var counts = new Dictionary<string, int>();

        foreach (var file in ClientFiles(root))
        {
            var matches = ComposedApiUrl.Matches(File.ReadAllText(file)).Count;
            if (matches > 0)
            {
                counts[Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')] = matches;
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
