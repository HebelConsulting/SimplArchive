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
// This was a RATCHET, like ClientHypermediaTests: 264 literals recorded per file at adoption, a file's count
// changing IN EITHER DIRECTION failing the build, so the work stayed a readable number rather than "partly
// localised". THE BUDGET IS NOW EMPTY — Home.razor's 167 went first (the workbench's snackbars and dialog
// titles), then the desktop's 11 views, then the web dialogs — so the assertion has flipped from "no more than
// the recorded count" to "none at all", and any new unkeyed literal fails the build wherever it appears.
//
// Roughly 30 of that original 264 were never real: the AXAML regex was matching inside longer attribute names
// and counting `SizeToContent="Height"` as user-facing text (see AxamlTextLiteral). A ledger is only worth
// keeping if its number means something, so the regex was fixed rather than the entries quietly deleted.
//
// What it scans, and why only these three: they are the shapes that are unambiguously user-facing and mechanically
// detectable. A bare literal in C# might be a log message, a CSS class or a test fixture, so scanning for those
// would produce noise, and a guard that cries wolf gets switched off. Consequently the ledger UNDERSTATES the
// problem — MainWindowViewModel's hard-coded rights labels ("Create external links", "Override checkout") are
// real and uncounted. Lowering this to zero is necessary, not sufficient.
public partial class LocalizationLiteralTests
{
    // Snackbar.Add(…) — the runtime feedback a user meets while ACTING, which is where the gap was found.
    //
    // NOT a regex, and the reason is worth keeping. The original was @"Snackbar\.Add\(\s*\$?""" — the quote had
    // to follow the paren — so it saw only the simplest shape and reported ZERO while nineteen literals sat in
    // the clients, every one of them the first arm of a ternary:
    //
    //     Snackbar.Add(resp.StatusCode == HttpStatusCode.Forbidden
    //         ? "You don't have permission to do that." : "Could not add the member.", Severity.Warning);
    //
    // That is the same failure as the hypermedia ledger's missing leading-slash spelling: a guard that cannot
    // see a whole shape is not a guard, and this one was worse for reporting zero — "finished" is a much more
    // convincing lie than "41 remaining".
    //
    // Widening the regex does not work, and the attempts are instructive. Allowing the literal to float away
    // from the paren means the CONTENT class must exclude ';' and newlines or a "literal" spans two statements.
    // Even then, quote parity defeats it: in `Get(x ? "KeyA" : "KeyB")` the text BETWEEN the two literals is
    // `" : "`, which any floating pattern happily reads as a literal containing a space. The scan has to know
    // which quotes open a literal and which close one, and that is tokenization, not matching.
    //
    // The prose test is what makes this decidable without also parsing Strings.Get: a resource key is a single
    // PascalCase token, so it never contains a space and never ends in a full stop. User-facing text does one or
    // the other. `Strings.Get(isGroup ? "StGroupCreated" : "StUserCreated")` is therefore silent, while
    // `isGroup ? "Group created." : "User created."` is not — with no special-casing of the call at all.
    private static int SnackbarLiteralCount(string text) => StatementsOf(text, "Snackbar.Add(").Count(HasProseLiteral);

    /// <summary>Each <paramref name="call"/> occurrence's text up to the end of its statement.</summary>
    private static IEnumerable<string> StatementsOf(string text, string call)
    {
        for (var i = text.IndexOf(call, StringComparison.Ordinal); i >= 0; i = text.IndexOf(call, i + 1, StringComparison.Ordinal))
        {
            var end = text.IndexOf(';', i);
            yield return end < 0 ? text[i..] : text[i..end];
        }
    }

    /// <summary>
    /// Whether a statement carries a literal that reads as PROSE — a space in it, or a closing full stop. Walks
    /// the text tracking quote parity so the gap between two adjacent literals is never mistaken for one.
    /// </summary>
    private static bool HasProseLiteral(string statement)
    {
        for (var i = 0; i < statement.Length; i++)
        {
            if (statement[i] == '\\')
            {
                i++; // an escaped character is never a delimiter
                continue;
            }

            if (statement[i] != '"')
            {
                continue;
            }

            var close = i + 1;
            while (close < statement.Length && statement[close] != '"')
            {
                close += statement[close] == '\\' ? 2 : 1;
            }

            if (close >= statement.Length)
            {
                return false; // unterminated: a multi-line literal, which this scan does not judge
            }

            var literal = statement[(i + 1)..close];
            if (literal.Contains(' ') || literal.EndsWith('.'))
            {
                return true;
            }

            i = close; // resume AFTER the closing quote — this is the parity that a regex cannot keep
        }

        return false;
    }

    // DialogService.ShowAsync<T>("…") — a dialog title passed positionally rather than via Strings.Get.
    [GeneratedRegex(@"ShowAsync<[^>]+>\(\s*\$?""")]
    private static partial Regex DialogTitleLiteral();

    // Avalonia markup: a text-bearing attribute set to a literal rather than {loc:Tr …}. Requires a
    // capital-then-lowercase start so it does not match style values, brushes or enum names.
    //
    // The (?<![A-Za-z]) matters more than it looks. Without it the alternation matches INSIDE a longer attribute
    // name, and the ledger fills with things no user ever reads: `SizeToContent="Height"` was counted as
    // user-facing text in ~30 files (the `Content` branch, capturing the layout value) — nearly every desktop
    // dialog's entry. The same accident cut the other way too: `PlaceholderText="Reviewer…"` was caught only
    // because `Text` matched inside `PlaceholderText`, so a real untranslated string was found by luck. It is
    // listed explicitly now, and the boundary keeps the count honest in both directions (issue #423).
    [GeneratedRegex(@"(?<![A-Za-z])(?:Content|Text|Header|ToolTip\.Tip|Watermark|Title|PlaceholderText)=""[A-Z][a-z][^""{]{2,}""")]
    private static partial Regex AxamlTextLiteral();

    // Text that must NOT be translated, so counting it would make the ledger unreachable rather than informative:
    // the product name, the vendor's own name and postal address in the About dialog, and a third-party tool's
    // name. Kept as an explicit list with this reason rather than silently widened regexes.
    private static readonly string[] NotTranslatable =
    [
        "SimplArchive",            // the product's own name
        "Hebel Consulting GmbH",   // the vendor's registered name
        "Schweighofplatz 7",       // the vendor's postal address (About dialog)
        "Beyond Compare",          // a third-party diff tool, named as itself
    ];

    // Empty, and meant to stay that way: every user-facing literal in both clients is keyed. An entry here is
    // now only a temporary parking space if a large conversion has to land in stages — add one, and remove it in
    // the same series of commits. Never add one to make a new literal pass.
    private static readonly Dictionary<string, int> Budget = new();

    [Fact]
    public void No_client_carries_an_unkeyed_user_facing_literal()
    {
        var root = RepoPaths.Root();
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
            "User-facing text must come from SimplArchive.Localization (issue #423):\n" + string.Join("\n", problems));
    }

    // A tripwire on the headline number, so a bulk regression cannot hide behind per-file budgets.
    [Fact]
    public void The_total_is_still_zero()
    {
        var total = CountByFile(RepoPaths.Root()).Values.Sum();
        Assert.True(total <= Budget.Values.Sum(),
            $"Clients carry {total} unkeyed user-facing literals; the budget allows {Budget.Values.Sum()}.");
    }

    // Anti-vacuous: if the regexes stop matching, every assertion above passes while checking nothing.
    //
    // This used to be "total > 50", which was really a bet that the work would never get done — and it has been:
    // the count is ZERO, so that tripwire would now fail the build for FINISHING the task it exists to track, and
    // the obvious repair (lower the number) just walks the same bet forward. Testing the regexes against samples
    // instead holds at any remaining count, which is the only version of this guard that survives success.
    [Fact]
    public void The_scanner_still_recognises_what_it_is_looking_for()
    {
        Assert.Equal(1, SnackbarLiteralCount("""Snackbar.Add("Could not save.", Severity.Error);"""));
        Assert.Equal(1, SnackbarLiteralCount("""Snackbar.Add($"Saved '{name}'.", Severity.Success);"""));

        // The ternary shape the old regex could not see — nineteen of these were live when it reported zero.
        Assert.Equal(1, SnackbarLiteralCount(
            """Snackbar.Add(forbidden ? "You don't have permission." : "Could not add the member.", Severity.Warning);"""));
        // A single word with a full stop still counts: "Unfollowed." is text, not a key.
        Assert.Equal(1, SnackbarLiteralCount("""Snackbar.Add(on ? "Following." : "Unfollowed.", Severity.Success);"""));
        Assert.Single(DialogTitleLiteral().Matches("""ShowAsync<RenameDialog>("Rename", parameters)"""));
        Assert.Single(AxamlTextLiteral().Matches("""<Button Content="Set reminder" />"""));
        Assert.Single(AxamlTextLiteral().Matches("""<TextBox PlaceholderText="Reviewer…" />"""));
        Assert.Single(AxamlTextLiteral().Matches("""<Button ToolTip.Tip="Next match" />"""));

        // …and does not fire on what it must ignore: a localised value, a layout property whose value merely
        // looks like a word (the SizeToContent="Height" trap), or an enum-ish value.
        Assert.Empty(AxamlTextLiteral().Matches("""<Button Content="{loc:Tr ReminderSet}" />"""));
        Assert.Empty(AxamlTextLiteral().Matches("""<Window SizeToContent="Height" CanResize="False" />"""));
        Assert.Empty(AxamlTextLiteral().Matches("""<TextBlock TextWrapping="Wrap" />"""));
        Assert.Equal(0, SnackbarLiteralCount("""Snackbar.Add(Strings.Get("StSaved"), Severity.Success);"""));

        // The keyed ternary — the CORRECT form, and the one a floating regex reads as a literal because the
        // text between the two keys is `" : "`. Quote parity is the whole difference.
        Assert.Equal(0, SnackbarLiteralCount(
            """Snackbar.Add(Strings.Get(isGroup ? "StGroupCreated" : "StUserCreated"), Severity.Success);"""));
        Assert.Equal(0, SnackbarLiteralCount(
            """Snackbar.Add(string.Format(Strings.Get("StFiledItem"), item.Name), Severity.Success);"""));
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
                var n = SnackbarLiteralCount(text)
                    + DialogTitleLiteral().Matches(text).Count
                    + AxamlTextLiteral().Matches(text).Count(m => !NotTranslatable.Any(m.Value.Contains));
                if (n > 0)
                {
                    counts[Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')] = n;
                }
            }
        }

        return counts;
    }

}
