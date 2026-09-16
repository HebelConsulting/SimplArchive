using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// A client must send AT a rel, never at an href it pairs with a verb of its own choosing (#1192).
//
// ADR 0543 says rel names are the compatibility surface, and #1173 cashed that at scale: fourteen routes moved
// and NO client learned a new address. The address half is true. The METHOD half was never true — a Link
// carries a `Method`, no client read it, and every call site named the verb itself. So a route that kept its
// rel and changed only its verb still broke every client, and nine call sites had to be corrected by hand.
//
// The fix is a helper that sends at a rel (Links.SendAsync on the web, ApiCore.SendRelAsync on the desktop),
// where the address and the method come from the same link and cannot disagree. This guard is the burn-down.
//
// WHY A PER-FILE BUDGET rather than a flat ban, in the shape ClientHypermediaTests established: there are 247
// sites across 77 files, so a ban would fail the build today and be suppressed tomorrow. A file's number may
// only go DOWN; converting a site lowers it in the same commit, and a file that reaches zero loses its entry.
// A number that goes UP is a NEW site written the old way, which is what this exists to stop.
//
// IT SHIPPED AT 181, THEN 247, AND BOTH NUMBERS WERE WRONG IN DIFFERENT WAYS.
//
// 181 was too NARROW: the detector wanted a recognisable rel-derived expression at the call site, so it missed
// every address passed through a parameter (`MoveIntrayItemAsync(moveUrl, …)`), a helper (`PostBulkAsync`, one
// method hardcoding POST for five operations), or a chained expression. When ADR 0797 changed six routes'
// METHODS, five call sites kept the old verb and answered 405 — in BOTH clients, in shipped features — and not
// one of the five was in the ledger.
//
// 247 was too BROAD, and that is the more dangerous error, because it pointed at work that must not be done.
// It counted every non-literal address, but most rels are NOT an instruction about the verb:
//
//   * A rel advertised with a WRITE is the server stating the action — `sort` is a PUT, `purge` a POST, the
//     bulk `delete` a DELETE. A client naming its own verb there can DISAGREE with the server, and did.
//   * A rel advertised as GET is an ADDRESS, and the verb is the caller's intent. ADR 0719 mandates exactly
//     that — one rel per resource, the method says which action — so `mask`, `index-data`, `parent`,
//     `notificationPreferences` and `webdavPassword` are advertised GET while clients legitimately PUT, POST
//     and DELETE to them. Measured: 81 of 158 rels are GET-only.
//   * A rel advertised with SEVERAL methods (`self`, `tags`, `settings`) cannot tell a caller which it wants.
//
// Converting a GET-advertised site is not merely wasted work: it sends a GET where a PUT was meant, which is
// how a notification-preferences save started failing. The over-broad ledger actively caused that.
//
// So this counts ONLY sites whose address names a rel the server advertises with exactly one method, that
// method being a write. 42 across 19 files.
//
// WHAT IT STILL CANNOT SEE, measured rather than assumed: the rel name must appear IN the sending line. A site
// that resolves the rel on a previous line — `var href = RelHref(row, "make-searchable"); … PostAsync(href)` —
// is invisible, and a control run confirms it. So 42 is a floor, not a total. Widening it would mean tracking
// a local across statements, which a text scan cannot do honestly; the alternative of guessing would put this
// straight back into the over-broad failure above.

public partial class ClientLinkMethodTests
{
    // A mutating send ON AN HTTP CLIENT, capturing the address expression it is given.
    //
    // The receiver matters, and its absence made an earlier version of this wrong in BOTH directions: without
    // it the pattern also matched method DECLARATIONS — `Task PutAsync(HttpClient http, …)` — which inflated a
    // trial count to 284.
    [GeneratedRegex(@"\b(?:_?[Hh]ttp|[Cc]lient|api|core\.Http|_core\.Http|Http)\s*\.\s*(Post|Put|Delete)(?:AsJson)?Async\(\s*([^;]{0,160})")]
    private static partial Regex MutatingSend();

    [GeneratedRegex(@"""([a-zA-Z][\w:-]*)""")]
    private static partial Regex QuotedName();

    // How the server advertises each rel, read from the Link(...) literals in the Api project.
    [GeneratedRegex(@"Link\(\s*""([a-zA-Z][\w:-]*)""\s*,[^,]+,\s*""(GET|PUT|POST|DELETE|HEAD)""")]
    private static partial Regex AdvertisedLink();

    // Sites that still name their own verb, per file. THIS MAY ONLY GO DOWN.
    private static readonly Dictionary<string, int> Budget = new(StringComparer.Ordinal)
    {
        // EMPTY, and that is the finished state rather than a missing ledger. Every entry was paid: 42 sites
        // across 19 files, burnt down in three tranches (desktop #1236, web #1237, the last seven here).
        //
        // It is therefore now a FLAT BAN, without changing a line of the checking logic — a file absent from
        // this dictionary has a budget of zero, so any site at all fails. The original comment explained that a
        // ban was not viable "because there are 247 sites across 77 files, so a ban would fail the build today
        // and be suppressed tomorrow". That objection expired with the debt.
        //
        // A NEW ENTRY HERE IS A REGRESSION, not a starting point. If one is genuinely needed — a call site that
        // cannot send at a rel for a reason worth recording — put the reason beside it, because an unexplained
        // number here is indistinguishable from someone silencing the guard.
    };

    /// <summary>
    /// The rels that NAME AN ACTION: advertised with exactly one method, and that method is a write.
    /// </summary>
    /// <remarks>
    /// This is the distinction the first version of this guard did not make, and getting it wrong cost real
    /// bugs in both directions.
    ///
    /// A rel advertised with a WRITE is the server stating the verb — `sort` is a PUT, `purge` a POST, the bulk
    /// `delete` a DELETE. A client that names its own verb there can disagree with the server, and did: when
    /// ADR 0797 changed six routes' methods, five such call sites kept the old verb and answered 405.
    ///
    /// A rel advertised as GET is an ADDRESS, and the verb is the caller's intent. ADR 0719 mandates exactly
    /// that — one rel per resource, the method says which action — so `mask`, `index-data`, `parent`,
    /// `notificationPreferences` and `webdavPassword` are all advertised GET while clients legitimately PUT,
    /// POST and DELETE to them. Counting those as debt is not merely noise: CONVERTING one sends a GET where a
    /// PUT was meant, which is how a notification-preferences save started failing.
    ///
    /// Rels advertised with SEVERAL methods (`self`, `tags`, `settings`) are excluded for the same reason —
    /// the link cannot tell a caller which of them it wants.
    /// </remarks>
    private static HashSet<string> ActionRels(string root)
    {
        var byRel = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "SimplArchive.Api"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Replace(Path.DirectorySeparatorChar, '/').Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match m in AdvertisedLink().Matches(File.ReadAllText(file)))
            {
                if (!byRel.TryGetValue(m.Groups[1].Value, out var methods))
                {
                    byRel[m.Groups[1].Value] = methods = new HashSet<string>(StringComparer.Ordinal);
                }

                methods.Add(m.Groups[2].Value);
            }
        }

        return [.. byRel.Where(r => r.Value.Count == 1 && !r.Value.Contains("GET") && !r.Value.Contains("HEAD")).Select(r => r.Key)];
    }

    [Fact]
    public void No_client_pairs_an_advertised_address_with_a_verb_of_its_own()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var actionRels = ActionRels(root);
        Assert.True(actionRels.Count > 40,
            $"Only {actionRels.Count} action rels found — the Link(...) scan stopped seeing the Api, which would "
            + "make this pass vacuously.");

        var counted = new Dictionary<string, int>(StringComparer.Ordinal);
        var scanned = 0;   // client files actually read — proves the scan reaches them
        var sends = 0;     // mutating sends the pattern matched at all — proves the pattern is alive
        foreach (var client in new[] { "SimplArchive.Client", "SimplArchive.DesktopClient" })
        {
            var dir = Path.Combine(root, "src", client);
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", StringComparison.Ordinal) && !file.EndsWith(".razor", StringComparison.Ordinal))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                scanned++;
                var n = 0;
                foreach (var line in File.ReadAllLines(file))
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("@*", StringComparison.Ordinal)
                        || trimmed.StartsWith("*", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (MutatingSend().Match(line) is not { Success: true } m)
                    {
                        continue;
                    }

                    sends++;
                    if (QuotedName().Matches(m.Groups[2].Value).Any(q => actionRels.Contains(q.Groups[1].Value)))
                    {
                        n++;
                    }
                }

                if (n > 0)
                {
                    counted[relative] = n;
                }
            }
        }

        // ANTI-VACUOUS, rewritten because the burn-down broke the original. It asserted that MORE THAN TEN files
        // still carried debt — which was true when 19 did, and became false the moment the debt was paid. A
        // guard whose liveness check is "plenty of violations remain" fails at exactly the moment it succeeds,
        // and reads as a broken detector rather than as a finished job.
        //
        // What actually needs proving is that the SCAN still reaches the clients and the PATTERN still matches
        // something — neither of which depends on how much debt is left. So: the files were found and read, and
        // the mutating-send pattern still fires somewhere in them (converted sites stop matching, but every
        // client still issues plenty of sends on addresses that are not rel-derived at all).
        Assert.True(scanned > 100,
            $"Only {scanned} client files were scanned — the scan stopped reaching the clients, which would "
            + "make this pass vacuously however the pattern behaves.");

        Assert.True(sends > 50,
            $"The mutating-send pattern matched only {sends} times across {scanned} client files. The debt may "
            + "legitimately reach zero, but the PATTERN going dead is a different thing and must not look the "
            + "same: clients still issue many sends on addresses that are not rel-derived.");

        var grew = counted
            .Where(c => c.Value > Budget.GetValueOrDefault(c.Key))
            .Select(c => $"  {c.Key}: {Budget.GetValueOrDefault(c.Key)} -> {c.Value}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        Assert.True(grew.Count == 0,
            "These files gained a call site that names its own verb on an advertised address (#1192):\n"
            + string.Join("\n", grew)
            + "\n\nSend AT the rel instead — Links.SendAsync (web) or ApiCore.SendRelAsync (desktop) — so the"
            + "\nmethod comes from the same link as the address and the two cannot disagree.");

        // The other direction, which is what makes it a burn-down rather than a list that rots.
        var paid = Budget
            .Where(b => counted.GetValueOrDefault(b.Key) < b.Value)
            .Select(b => $"  {b.Key}: {b.Value} -> {counted.GetValueOrDefault(b.Key)}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        Assert.True(paid.Count == 0,
            "These files have FEWER such sites than their budget. Lower the budget in the same commit that\n"
            + "converted them (delete the entry at zero), or the ledger stops describing anything:\n"
            + string.Join("\n", paid));
    }
}
