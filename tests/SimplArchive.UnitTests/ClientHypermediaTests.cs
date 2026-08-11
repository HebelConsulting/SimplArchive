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
        // 51 = 48 + 3 after the workbench page was decomposed (ADR 0558). The budget is keyed by FILE, so
        // moving code moves composed URLs — and the two numbers still summing to 51 is what proves the
        // extraction was a pure move rather than a rewrite that quietly gained or lost a call.
        ["src/SimplArchive.Client/Pages/Home.razor"] = 48,
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
