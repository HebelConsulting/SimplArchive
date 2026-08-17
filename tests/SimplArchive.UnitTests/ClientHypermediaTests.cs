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

    // THE END STATE (#416): the budget dictionary this test carried for ten months is GONE, because the debt it
    // measured is gone — the web burn-down finished at 26 → 0 and the desktop's finished earlier at 184 → 1.
    // What remains is not a budget but a set of NAMED, COUNTED exceptions: each is a single deliberate line, and
    // a file's count moving in EITHER direction fails the build. A budget says "this much debt is tolerated"; a
    // named exception says "exactly this line, for exactly this reason".
    //
    //   • ApiRoot — THE ENTRY POINT, permanent. ADR 0543's rule is that the only URL a client may know is the
    //     API root, and this is the one line that knows it. Counted rather than exempted so that a second URL
    //     appearing in the file fails the build.
    //   • BrowseService.FetchAsync — the id→resource turn: a rel can be followed only from a resource you
    //     already have, so a caller holding ONLY an id must fetch once before it can follow anything. It is
    //     deliberately the single place the web client writes that address, because two copies of the one
    //     sanctioned line is how a rule with one exception quietly acquires a second.
    //
    // The DESKTOP has no entry at all any more: its one composed URL (DocumentAddress, the id→resource turn)
    // was retired by #443's endgame — every DocumentsClient method takes an address a listing row, a payload
    // or the document resource advertised, and the id-bearing payloads (a notification, a task, a reminder, a
    // search hit, a hold item, an external-link row) advertise the document's address themselves.
    private static readonly Dictionary<string, int> NamedExceptions = new()
    {
        ["src/SimplArchive.Client/Services/ApiRoot.cs"] = 1,
        ["src/SimplArchive.Client/Services/BrowseService.cs"] = 1,
    };

    [Fact]
    public void No_client_composes_an_api_url_except_the_named_exceptions()
    {
        var actual = CountByFile();
        var offenders = new List<string>();

        foreach (var (file, count) in actual.OrderBy(kv => kv.Key))
        {
            var allowed = NamedExceptions.GetValueOrDefault(file);
            if (count > allowed)
            {
                offenders.Add($"  {file}: {count} composed api/ URL(s), {allowed} allowed — follow a link rel "
                    + "from the resource instead of composing the URL (ADR 0543). If the caller holds only an id, "
                    + "make it hold the ROW (ADR 0555), or add the rel to whatever handed it that id.");
            }
            else if (count < allowed)
            {
                offenders.Add($"  {file}: {count} composed api/ URL(s) where the named exception expects {allowed} "
                    + "— the exceptional line is gone. Good news: delete its NamedExceptions entry so the "
                    + "carve-out dies with the line it named (and if it was DocumentAddress, close #443).");
            }
        }

        foreach (var file in NamedExceptions.Keys.Where(f => !actual.ContainsKey(f)).OrderBy(f => f))
        {
            offenders.Add($"  {file}: composes nothing (or the file moved) — delete its NamedExceptions entry.");
        }

        Assert.True(offenders.Count == 0,
            "The hypermedia end-state rule is violated (ADR 0543, #416):\n" + string.Join("\n", offenders));
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

            foreach (Match m in RelFollowedByHref().Matches(text))
            {
                demanded.Add(m.Groups["rel"].Value);
            }

            foreach (Match m in RelFromRelMap().Matches(text))
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

        // The two forms that were invisible until 2026-08-11 — between them, 32 of the client's rels.
        Assert.Single(RelFollowedByHref().Matches("""Links.Href(c.Links, "working-copy")"""));
        Assert.Single(RelFollowedByHref().Matches("""Links.Required(detail.Links, "index-data")"""));
        Assert.Single(RelFollowedByHref().Matches("""Links.Href(Links.RelMap(d.Links), "checkin")"""));
        Assert.Single(RelFromRelMap().Matches("""Detail.Links?.GetValueOrDefault("reminders")"""));

        // …and HrefAsync must NOT also match as the plain Href form, or every root rel is counted twice.
        Assert.Empty(RelFollowedByHref().Matches("""await ApiRoot.HrefAsync("externalLinks")"""));

        // The advertised side reads BOTH constructions. The target-typed one is what the controllers use inside
        // a List<Link> initialiser, and requiring the space after `new` hid every one of them.
        Assert.Single(AdvertisedRel().Matches("""new Link("self", url, "GET")"""));
        Assert.Single(AdvertisedRel().Matches("""new("subscription", $"/api/documents/{id}/subscription", "GET")"""));

        // Hyphenated rels must be seen: the earlier pattern was [A-Za-z]+, which silently could not match one.
        Assert.Equal("external-links", RelFollowed().Match("""RequireAsync("external-links")""").Groups["rel"].Value);
    }

    // Every call shape that takes a rel NAME. RequireMeAsync/MeHrefAsync are spelled out: neither contains
    // "RequireAsync" or is matched by it, which is how the me-rel family stayed invisible (#431).
    [GeneratedRegex(@"(?:RequireMeAsync|MeHrefAsync|RootHrefAsync|RequireAsync|HrefAsync)\(""(?<rel>[A-Za-z0-9_-]+)""")]
    private static partial Regex RelFollowed();

    // The comparison form: scanning a resource's links for a rel rather than asking ApiRoot for it.
    // Links.Href(links, "x") and Links.Required(links, "x") — the web client's COMMONEST form, and it was
    // unmatched until 2026-08-11. The alternation above deliberately lists HrefAsync, which reads as though the
    // plain Href were covered too; it is not, and the (?<!Async) is what keeps the two apart.
    //
    // 32 of the client's rels were reachable only this way, so roughly two in five were invisible to the guard
    // whose entire job is checking rel names — the third blind spot of this exact shape found in one day, after
    // the composed-URL regex missing the leading-slash spelling and the localization scan missing a ternary. The
    // pattern is always the same: the guard covers the shape its author had in front of them, the comment then
    // claims completeness, and the claim is what stops anyone re-measuring.
    [GeneratedRegex(@"(?<!Async)\b(?:Href|Required)\((?:[^()""]|\([^()]*\))*,\s*""(?<rel>[A-Za-z0-9_-]+)""")]
    private static partial Regex RelFollowedByHref();

    // Detail.Links?.GetValueOrDefault("x") — a rel read straight out of an already-fetched rel map (ADR 0555).
    [GeneratedRegex(@"GetValueOrDefault\(""(?<rel>[A-Za-z0-9_-]+)""\)")]
    private static partial Regex RelFromRelMap();

    [GeneratedRegex(@"\.Rel\s*(?:==|is)\s*""(?<rel>[A-Za-z0-9_-]+)""")]
    private static partial Regex RelCompared();

    // How the API advertises one: `new Link("rel", …)`, or `new("rel", …)` inside a link collection. The href
    // argument is deliberately unconstrained — it is a literal, an interpolation, or a Url.Action(...) call
    // (that last one is how every paginated `next` is built), and pinning its shape only narrows what counts as
    // "advertised" for no gain.
    // Both spellings of the construction: `new Link("x", …)` and the target-typed `new("x", …)`. The literal
    // space after `new` was required until 2026-08-11, so every target-typed advertisement was invisible — and
    // the controllers use that form freely inside a `List<Link>` initialiser. Seven rels (subscription, export,
    // retention, verify, worm-verify, add-member, referencing-folders) read as "advertised nowhere" the moment
    // the client side of this guard was widened enough to look for them. Both halves had the same blind spot,
    // which is why neither exposed the other.
    [GeneratedRegex(@"new\s*(?:Link)?\s*\(\s*""(?<rel>[A-Za-z0-9_-]+)"",")]
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
