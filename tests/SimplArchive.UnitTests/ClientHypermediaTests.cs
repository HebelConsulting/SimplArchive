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
        ["src/SimplArchive.Client/Dialogs/CompareCheckoutDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/CompareVersionsDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/FilingDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/FolderPickerDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/InboxSendDialog.razor"] = 2,
        ["src/SimplArchive.Client/Dialogs/ManageAccessDialog.razor"] = 6,   // 7 → 6 (#426): the inheritance PUT follows the acl-inheritance rel
        ["src/SimplArchive.Client/Dialogs/PasskeysDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/ProfilePhotoDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/ReferencesDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/ReminderDialog.razor"] = 4,
        ["src/SimplArchive.Client/Dialogs/SensitivityLabelsDialog.razor"] = 3,
        ["src/SimplArchive.Client/Dialogs/ServiceAccountsDialog.razor"] = 3,
        ["src/SimplArchive.Client/Dialogs/VersionsDialog.razor"] = 1,
        ["src/SimplArchive.Client/Dialogs/WorkflowDialog.razor"] = 2,
        ["src/SimplArchive.Client/Layout/MainLayout.razor"] = 6,
        ["src/SimplArchive.Client/Pages/Home.razor"] = 99,    // 101 → 99 (#416): folder-follow fetches the resource and follows its subscription rel   // 108 → 101 (#416): the detail pane follows rels — the row's for mask/index-data, the resource's for tags/subscription
        // 184 → 183 (issue #385): the desktop read the document resource TWICE — once for its name, once for its
        // sensitivity label — so the per-document external-links rel had nowhere to be picked up from. One read
        // now serves both and carries the rel, which is what let the dialog follow it instead of composing a URL.
        //
        // 151 → 145 (issue #416): the document resource advertises `tags`, `reminders` and `subscription`, and
        // every read and write follows one. Six literals for one shared DocumentRelAsync helper — the callers
        // that hold only an id fetch the resource once and follow the rel, instead of composing a path per
        // sub-resource. The detail pane, which already holds the resource, pays nothing. Done as ONE change — rel, carried into DocumentDetailInfo, threaded to every call site —
        // because splitting those is what produced the Node.Links-always-null regression: the model gained a
        // field, the call sites used it, and nothing verified the value arrived.
        //
        // 152 → 151 (issue #416, tranche B): GetVersionsAsync takes the advertised href. The enabling change is
        // on the SERVER — a listed item now advertises its own unconditional sub-resources, so a client holding a
        // row has addresses rather than just an id. Without that, following a rel would have cost a `self` fetch
        // per row, and paying two calls to follow one rel is how a codebase talks itself back into string paths.
        //
        // 153 → 152 (issue #426): SetInheritanceAsync takes the advertised href instead of composing the path.
        // The rel is CONDITIONAL, so following it also removes an affordance that could only ever fail — the
        // clients no longer offer to break inheritance on a repository root.
        //
        // 183 → 153 (issue #416, tranche A): the 19 top-level COLLECTION roots now come from the API root's own
        // rels via the cached RootHrefAsync. What remains here is overwhelmingly the interpolated kind
        // ($"api/documents/{id}/…"), which needs a resource in hand rather than a path — the structural half of
        // the burn-down, and a separate piece of work.
        ["src/SimplArchive.DesktopClient/Services/SimplArchiveApiClient.cs"] = 145,
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

    // Every rel a client follows must actually be advertised by the API.
    //
    // Following a rel that does not exist is SILENT, which is what makes this worth a build-time check. ADR 0543
    // makes a missing rel meaningful — it means "not available to you, here, now", so a client correctly hides
    // the affordance — and that is exactly why a TYPO is indistinguishable from a legitimate absence at runtime.
    // The rule that makes the design good is the rule that makes the mistake invisible. The one time it did
    // surface loudly was luck: converting tranche A of #416, nine of ten rels were added and `tags` was missed,
    // and the only signal was two desktop tag tests failing minutes later.
    //
    // Covers every way a client reaches a rel, because it used to cover only one of three (issue #431):
    //   • the root/me call forms — HrefAsync("x"), RequireAsync("x"), RequireMeAsync("x"), MeHrefAsync("x");
    //   • the comparison form — Links.FirstOrDefault(l => l.Rel == "x"), which is how the workflow transitions
    //     and acl-inheritance are followed.
    // `RequireMeAsync` is not a superstring of `RequireAsync`, so the whole me-rel family (11 rels) was invisible
    // to the old alternation; the comparison form (8 rels) was never matched at all. That was ~19 of ~39 rels
    // unchecked — none of them broken, which is precisely how it went unnoticed.
    //
    // Compared against rels advertised ANYWHERE in the Api, not just the resource that ought to carry each one.
    // Checking rel-to-resource would need the client's context (which resource it holds), which a regex does not
    // have; the achievable check is "this name exists in the API's vocabulary", and that is what catches the
    // realistic failures — a typo and a server-side rename.
    [Fact]
    public void Every_rel_the_clients_follow_is_advertised_by_the_api()
    {
        var root = RepoRoot();
        var demanded = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in ClientFiles(root))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in RelFollowed().Matches(text))
            {
                demanded.Add(m.Groups["rel"].Value);
            }

            foreach (Match m in RelCompared().Matches(text))
            {
                demanded.Add(m.Groups["rel"].Value);
            }
        }

        var advertised = new HashSet<string>(StringComparer.Ordinal);
        var apiDir = Path.Combine(root, "src", "SimplArchive.Api");
        foreach (var file in Directory.EnumerateFiles(apiDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            foreach (Match m in AdvertisedRel().Matches(File.ReadAllText(file)))
            {
                advertised.Add(m.Groups["rel"].Value);
            }
        }

        var missing = demanded.Where(r => !advertised.Contains(r)).ToList();

        Assert.True(missing.Count == 0,
            "These rels are followed by a client but advertised nowhere in the API. Following one yields null, so "
            + "the affordance silently never appears — no exception, no failing test (ADR 0543):\n  "
            + string.Join("\n  ", missing));

        Assert.True(demanded.Count > 25,
            $"expected the clients to follow many rels, found {demanded.Count} — the scan is probably broken");
    }

    // Anti-vacuous, sample-based rather than a count that grows with the work: a threshold on "how many rels
    // exist" is a bet on the codebase's size, and the same trap LocalizationLiteralTests had to be rescued from.
    [Fact]
    public void The_rel_scanner_still_recognises_every_form()
    {
        Assert.Single(RelFollowed().Matches("""await ApiRoot.RequireAsync("documents")"""));
        Assert.Single(RelFollowed().Matches("""await ApiRoot.HrefAsync("me")"""));
        Assert.Single(RelFollowed().Matches("""await ApiRoot.RequireMeAsync("changePassword")"""));
        Assert.Single(RelFollowed().Matches("""await ApiRoot.MeHrefAsync("webdavPassword")"""));
        Assert.Single(RelCompared().Matches("""Links.FirstOrDefault(l => l.Rel == "acl-inheritance")"""));
        Assert.Single(RelCompared().Matches("l.Rel is \"submit\""));

        // Hyphenated rels must be seen: the earlier pattern was [A-Za-z]+, which silently could not match one.
        Assert.Equal("external-links", RelFollowed().Match("""RequireAsync("external-links")""").Groups["rel"].Value);
    }

    // Every call shape that takes a rel NAME. RequireMeAsync/MeHrefAsync are spelled out: neither contains
    // "RequireAsync" or is matched by it, which is how the me-rel family stayed invisible (#431).
    [GeneratedRegex(@"(?:RequireMeAsync|MeHrefAsync|RootHrefAsync|RequireAsync|HrefAsync)\(""(?<rel>[A-Za-z0-9_-]+)""")]
    private static partial Regex RelFollowed();

    // The comparison form: scanning a resource's links for a rel rather than asking ApiRoot for it.
    [GeneratedRegex(@"\.Rel\s*(?:==|is)\s*""(?<rel>[A-Za-z0-9_-]+)""")]
    private static partial Regex RelCompared();

    // How the API advertises one: `new Link("rel", …)`, or `new("rel", …)` inside a link collection. The href
    // argument is deliberately unconstrained — it is a literal, an interpolation, or a Url.Action(...) call
    // (that last one is how every paginated `next` is built), and pinning its shape only narrows what counts as
    // "advertised" for no gain.
    [GeneratedRegex(@"new (?:Link)?\(\s*""(?<rel>[A-Za-z0-9_-]+)"",")]
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
