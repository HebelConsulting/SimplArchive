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
    // Matches a string literal that starts an API path: "api/…", $"api/…", or the leading-slash forms "/api/…".
    // Deliberately narrow — it catches the way both clients actually build these, without flagging prose in a
    // comment.
    //
    // The optional slash was MISSING until 2026-08-11, and seven composed URLs sat in the clients unseen because
    // of it — a bulk-endpoint family that happened to be written "/api/documents/bulk/move" rather than
    // "api/documents/bulk/move". A ledger that cannot see a whole spelling is not a ledger; the burn-down had
    // been reporting 41 when the real figure was 48.
    private static readonly Regex ComposedApiUrl = new("\\$?\"/?api/", RegexOptions.Compiled);

    // Two different states, deliberately, because the two clients are at two different points.
    //
    // THE DESKTOP CLIENT IS DONE. Its burn-down went 184 → 1, and the one that remains is a NAMED EXCEPTION
    // rather than a budget: `SimplArchiveApiClient.DocumentAddress`, the single line that turns a document id
    // back into a resource. A caller must do that when the only state it holds is a Guid, because a rel can be
    // followed only from a resource you already have.
    //
    // It is NOT irreducible in principle, and calling it that would hide real work behind a principle. ADR 0555's
    // answer is to hold the ROW, and then the address is `self`. It is irreducible only while (a) the desktop
    // view-model's state is still id-shaped — `_currentFolderId`, a restored selection — and (b) the payloads
    // that hand a client a bare document id (a notification, a task, a reminder, a search hit) do not also
    // advertise that document's address. Closing both retires the exception — issue #443 records how.
    //
    // Why an exception beats a budget of 1: a budget of 1 permits ANY one composed URL anywhere in the file.
    // This permits only that line, and fails the moment a second appears.
    private const string ExceptionFile = "src/SimplArchive.DesktopClient/Services/SimplArchiveApiClient.cs";

    private const int ExceptionCount = 1;

    // THE WEB CLIENT IS NOT DONE, so it keeps the ratchet: the count must EQUAL the budget, so adding one fails
    // and converting one fails until the number is lowered in the same commit. When this empties, delete the
    // dictionary and the rule becomes "no client composes an API URL, except the named line above".
    private static readonly Dictionary<string, int> Budget = new()
    {
        // 95 → 51 across earlier tranches (#416): the workbench's document, audit, tenant, tag, saved-search and
        // search-field families. What remains is dominated by the row actions that need the full document
        // RESOURCE rather than a listing row — checkout, move, set-primary-location — which cost a fetch per row
        // action, and by the version-compare pair. The desktop side solved the same problem by holding the row
        // (ADR 0555); the web client's equivalent migration has not been done.
        //
        // The budget is keyed by FILE, so decomposing the workbench page (ADR 0558) MOVES composed URLs between
        // entries rather than removing them — and the entries still summing to the same total is what proves an
        // extraction was a pure move rather than a rewrite that quietly gained or lost a call. The running sum:
        // The running sum, each step stating whether it MOVED or CONVERTED, because only both numbers make a
        // move provable: 51 = 48 + 3, then 51 = 44 + 4 + 3 (pure moves), then 50 = 43 + 4 + 3 (Search moved its
        // region AND converted the one URL in it), then 50 = 36 + 7 + 4 + 3 (Inbox, a pure move).
        //
        // Now 41 = 23 + 7 + 4 + 3 + 1 + 3. The Recycle bin moved AND converted all NINE of its URLs — the biggest single
        // drop since the desktop burn-down — because the server half landed first (#450 gave each row the four
        // detail addresses its pane reads; the listing envelope already carried the three bulk actions). It cost
        // no extra request either: both sets arrive with a listing that was already being read (ADR 0557). That
        // is what the server tranche was for, and it is why the client half was cheap.
        //
        // Then 41 = 20 + 2 + 1 + 7 + 4 + 3 + 1 + 3 — pure moves again, of the three on the browse path.
        //
        // Now 48 = 23 + 4 + 1 + 2 + 1 + 7 + 4 + 3 + 1 + 3. The jump is NOT new debt: it is the seven the ledger
        // could not see until the regex learned the leading-slash spelling, plus the API root's own entry point,
        // which is now counted rather than invisible. The burn-down's real figure was 48 all along.
        // 48, not 41: widening the regex to see the leading-slash spelling exposed seven that had never been
        // counted (see ComposedApiUrl). Three are the drag-drop handler's bulk posts, four the bulk bar's.
        ["src/SimplArchive.Client/Pages/Home.razor"] = 23,
        // The bulk endpoints, carried out of the page with the actions that post them (ADR 0558). They belong to
        // the web burn-down: /documents/bulk/* is a collection the API does not advertise a rel for yet, so
        // converting them is a server change first.
        ["src/SimplArchive.Client/Services/BulkActions.cs"] = 4,
        // THE ENTRY POINT, not a debt. ADR 0543 says the only URL a client may know is the API root, and this is
        // the one line that knows it — every other address in both clients is followed from a rel reached from
        // here. It is counted rather than exempted so that a SECOND URL appearing in this file fails the build.
        ["src/SimplArchive.Client/Services/ApiRoot.cs"] = 1,
        // The two the shared browse plumbing carried out of the page (ADR 0558): a folder's references
        // collection, and the ONE irreducible composition — fetching a document by id to follow its own
        // `children` rel, for the caller that holds an id and no row (ADR 0543).
        ["src/SimplArchive.Client/Services/BrowseService.cs"] = 2,
        // The admin personal-repositories listing, which the tree's Administration branch carried out with it.
        // Not reachable by rel today: it is the ONE listing the API root does not advertise, so converting it
        // is a server change (a rel on the root, gated on tenant-admin) rather than part of this move.
        ["src/SimplArchive.Client/Services/TreeState.cs"] = 1,
        // The three the shared row actions carried out of the page with them (ADR 0558) — the legal-hold
        // items collection and the reference endpoints, which belong to the web burn-down, not to the move.
        ["src/SimplArchive.Client/Services/DocumentActions.cs"] = 3,
        ["src/SimplArchive.Client/Components/Tabs/InboxTab.razor"] = 7,
        // The one the Users & groups tab carried out with it — a principal's own address, which belongs to the
        // web burn-down rather than to the extraction that moved it.
        ["src/SimplArchive.Client/Components/Tabs/UsersGroupsTab.razor"] = 1,
        // The check-out stash addresses — download, replace, check in, cancel. The rels exist server-side
        // (#441 renamed `checkin` to mean the POST that actually checks in); this is the client half.
        ["src/SimplArchive.Client/Components/Tabs/CheckoutTab.razor"] = 4,
        // The three legal-hold addresses: creating a matter for one document, adding an item, and removing
        // one. The rels exist server-side (#441 added the item's own `remove`); this is the client half,
        // which belongs to the web burn-down rather than to the extraction that carried them here.
        ["src/SimplArchive.Client/Components/Tabs/LegalHoldsTab.razor"] = 3,
    };

    [Fact]
    public void No_client_composes_an_api_url_except_the_one_named_exception()
    {
        var actual = CountByFile();
        var offenders = new List<string>();

        foreach (var (file, count) in actual.OrderBy(kv => kv.Key))
        {
            var allowed = file == ExceptionFile ? ExceptionCount : Budget.TryGetValue(file, out var b) ? b : 0;
            if (count > allowed)
            {
                offenders.Add($"  {file}: {count} composed api/ URL(s), {allowed} allowed — follow a link rel "
                    + "from the resource instead of composing the URL (ADR 0543). If the caller holds only an id, "
                    + "make it hold the ROW (ADR 0555), or add the rel to whatever handed it that id.");
            }
            else if (count < allowed && file != ExceptionFile)
            {
                offenders.Add($"  {file}: {count} composed api/ URL(s), budget {allowed} — good news: lower the "
                    + $"budget in ClientHypermediaTests to {count} so the ledger stays honest.");
            }
        }

        foreach (var file in Budget.Keys.Where(f => !actual.ContainsKey(f)).OrderBy(f => f))
        {
            offenders.Add($"  {file}: no composed api/ URLs left (or the file moved) — delete its budget entry.");
        }

        Assert.True(offenders.Count == 0,
            "A client is composing API URLs (ADR 0543):\n" + string.Join("\n", offenders));
    }

    // The exception is a debt, not a licence, so it is asserted to still BE one line. If it is ever converted,
    // this fails and the whole mechanism — both constants and both tests — is deleted rather than left behind
    // asserting nothing. An exception nobody is forced to revisit is how a temporary carve-out becomes permanent.
    [Fact]
    public void The_one_named_exception_is_still_exactly_one_line()
    {
        var actual = CountByFile();
        var count = actual.TryGetValue(ExceptionFile, out var c) ? c : 0;

        Assert.True(count == ExceptionCount,
            count == 0
                ? $"{ExceptionFile} composes no api/ URL any more — the last one is gone. Delete ExceptionFile, "
                  + "ExceptionCount and this test, and leave the rule as 'no client composes an API URL' (ADR 0543)."
                : $"{ExceptionFile} composes {count} api/ URLs, expected exactly {ExceptionCount} "
                  + "(DocumentAddress). A second one is a regression, not a new exception.");
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
