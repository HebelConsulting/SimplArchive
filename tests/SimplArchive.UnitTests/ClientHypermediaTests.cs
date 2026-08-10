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
        // 2 → 1 (#416, the web long tail): the version LIST is followed from the row's `versions` rel. The
        // compare call itself stays and is the last composed URL in the web client: a link names ONE resource,
        // and "these two versions against each other" has no advertised address. Converting it needs an API
        // shape that can express the pair — a compare rel per version, or the pair as query parameters — which
        // is a route change, not a client one.

        // 95 → 84 (issue #416): the users & groups administration family. A user row advertises
        // reset-password / reset-mfa / deactivate alongside rights and photo, a group row members / delete, and
        // a member row its own `remove` — the pair being the only thing that knows both ends of a membership.
        // Deleting a principal stopped branching on IsGroup to build two different paths: it follows whichever
        // rel its row carries. Adding a member is the one composition left, and cannot be a rel as the API
        // stands — the user being added is not in the members collection yet, so nothing the client holds can
        // advertise the address; that needs the member in the BODY of a POST (recorded on the issue).
        // 84 → 74 (issue #416): the detail pane and the row actions. The pane's own bootstrap read follows the
        // ROW's `self` rel, which then makes everything it loads next — versions, mask, index-data — a rel on the
        // resource it just read rather than a path rebuilt per call. Rename and delete follow the row's address,
        // and the ETag probe now HEADs the SAME address the mutation will PUT/DELETE, instead of a path
        // reconstructed from an id beside it.
        //
        // A reference row now advertises the TARGET document's `self` too, which is what makes rename/delete work
        // on one: without it the row had an id and no address, and the conversion above would have turned those
        // two actions into silent no-ops on a reference — caught before the suite ran, but only by asking which
        // rows lack the rel rather than assuming every row carries it. Its own `delete` rel is followed now too.
        //
        // Move and set-primary-location are deliberately still composed: both are rels on the full document
        // RESOURCE, and a listing row does not carry them (a listing is the wrong place to answer "may I?"), so
        // converting them costs a fetch per row action and belongs with the tranche that adds it.
        //
        // 74 → 53 (issue #416): every action that belongs TO a collection now rides on that collection, and the
        // rels are captured where the collection is READ — so the tab that already loaded the audit log, the
        // tenant settings or the saved searches holds the addresses of everything that can be done to them, at
        // no extra round trip. The audit log carries retention/export/purge/verify/worm-verify; tenant settings
        // its two maintenance actions; a saved search its rewrite/delete/shares, but only when it is yours; a
        // tag its rename/retire/merge; a retention row extend, and dispose only where no review is required.
        // The searchable-PDF backfill takes a ROOT rel instead — it hangs off no collection a client has read.
        // 53 → 51 (#416): the search-field catalogue follows the new root rel, and adding a group member
        // follows one too now that the API takes the member in the BODY of a POST to the members collection.
        ["src/SimplArchive.Client/Pages/Home.razor"] = 51,
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
        //
        // 141 → 125 (issue #416): the caller's own account. The desktop had no `me` helper at all — the web
        // client grew one an earlier tranche ago while this file kept composing thirteen /api/users/me/… paths,
        // so this is a parity fix as much as a burn-down (ADR 0511 treats a web/desktop pair as one surface).
        // A cached MeHrefAsync mirrors RootHrefAsync; password, photo, the three MFA calls, passkeys, the
        // WebDAV password, the personal repository and notification preferences all follow rels the `me`
        // resource already advertised. The last three come from rels added in the same change: a passkey row's
        // own `self`, and a sensitivity label's `retire`/`unretire` — where WHICH rel is present is the label's
        // state, so the row stopped deciding that from a `Retired` flag it interpreted itself.
        //
        // 125 → 118 (issue #416): the collection-action family, chosen because every one of these methods takes
        // NO id — so none of them changes a public signature, and none runs into the 31 test call sites that
        // stopped the users & groups family (recorded on the issue). The backfill follows a root rel; the
        // tenant-settings maintenance actions, the audit log's retention policy and the saved-search share
        // targets are rels ON the resource that owns them, so each reads that resource first. That read is the
        // trade the root's "collection roots only" rule asks for, and it is paid per admin click rather than per
        // screen. The audit one adds `?limit=1` to the advertised href so learning one number does not drag back
        // a page of audit events — a query on a rel's href, not a path this client invented.
        //
        // 118 → 106 (issue #416, ADR 0555): the surface migration. Methods now take the ROW they act on, which
        // carries the addresses the listing advertised, so the users & groups family follows rels instead of
        // rebuilding paths — the family that had to be REVERTED when it was attempted against the old id-based
        // surface. The creates return the row too: the create response is the resource, rels included, and
        // returning only its id was what forced every follow-up call to compose.
        //
        // The test migration is the bulk of this and the compiler could only find part of it. Three runtime-only
        // breakages remained after everything compiled: a row passed to AddWithValue (which takes object), a row
        // in `new { reviewerId }` serialised as a whole object, and a row interpolated into a URL. All three are
        // "a Guid became a row where the type system had stopped looking" — the desktop suite caught each.
        //
        // 106 → 96 (issue #416): with the surface migrated, the row-carrying families convert cheaply — tags,
        // saved searches and service accounts each act through the row that advertised the address, and a
        // revoked account or a search shared WITH you simply carries no write rels, so the affordance is the
        // server's answer rather than IsActive/IsMine re-derived here. The search-field catalogue moved to a
        // root rel: it is read before any search has run, so no search response exists to hang it off.
        // 96 → 94 (#416): the two addresses that could NOT be expressed as rels are gone, because the API was
        // reshaped rather than the client tricked. Comparing two versions is now GET /versions/compare?from=&to=
        // — one advertised address with the pair as parameters, since a link names one resource and a pair has
        // none. Adding a group member is POST /members with the user in the BODY, so the collection advertises
        // `add-member` and the chosen principal travels as data; the keyed PUT is retired rather than kept
        // beside it, because two ways to do one thing is how a client talks itself back into composing.
        ["src/SimplArchive.DesktopClient/Services/SimplArchiveApiClient.cs"] = 94,
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
