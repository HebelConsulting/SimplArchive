using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// IMAP shows exactly the information from the details pane (#1301).
//
// WHY THIS NEEDS A GUARD. The two surfaces present the same documents and are written months apart by whoever
// is touching one of them. They drifted: IMAP omitted the name, the file extension, the workflow status, the
// OCR status and the retention, ordered what remained differently, and carried a Size row the pane did not
// have. Nothing failed — both surfaces worked, each was internally consistent, and a reader comparing them got
// two overlapping answers to "what does the archive say about this document?". A mail client is exactly where
// that goes unnoticed, because it is the surface nobody opens to check.
//
// The order and the label keys now live once, in SimplArchive.Presentation.DocumentDetailRows, and IMAP builds
// its rows by iterating it — so IMAP cannot drift without changing the shared list. The PANE is Razor markup
// and renders each row with its own controls, so it cannot iterate the list without losing that; this reads
// the markup instead and checks it presents the same keys in the same order.
public class DetailRowParityTests
{
    private const string Pane = "src/SimplArchive.Client/Components/Panes/IndexDataPane.razor";

    [Fact]
    public void The_pane_presents_the_shared_rows_in_the_shared_order()
    {
        var keysInMarkup = LabelKeysInOrder(File.ReadAllText(Path.Combine(RepoRoot(), Pane)));
        var expected = DocumentDetailRows.InOrder.Select(DocumentDetailRows.LabelKey).ToList();

        Assert.Equal(expected, keysInMarkup);
    }

    [Fact]
    public void Every_row_names_a_resource_key_that_exists()
    {
        // EXISTENCE, not "the text differs from the key" — which is what this asserted first and was wrong.
        // `Name` translates to "Name" in all four languages, so comparing the resolved text against the key
        // reported a missing label for a perfectly good one. A guard whose failure mode is "a word that
        // happens to equal its key" is a guard that will be suppressed rather than read.
        //
        // Per-LANGUAGE coverage is LocalizationKeyTests' job and is not duplicated here; this asserts only the
        // claim this file is responsible for: every row in the shared list names a key that resolves at all.
        var known = Localization.Strings.AllKeys().ToHashSet(StringComparer.Ordinal);

        foreach (var row in DocumentDetailRows.InOrder)
        {
            var key = DocumentDetailRows.LabelKey(row);
            Assert.True(known.Contains(key),
                $"{row} names the label key \"{key}\", which is in no resource file — the pane and the IMAP "
                + "message would both print the raw key.");
        }
    }

    [Fact]
    public void A_header_name_is_never_the_translated_label()
    {
        // The X- header a client filters on must not move when someone translates a label. Derived from the
        // row's name, which is why this can assert it rather than hope.
        foreach (var row in DocumentDetailRows.InOrder)
        {
            var header = DocumentDetailRows.HeaderName(row);
            Assert.StartsWith("X-SimplArchive-", header, StringComparison.Ordinal);
            Assert.DoesNotContain(" ", header, StringComparison.Ordinal);
            Assert.Equal($"X-SimplArchive-{row}", header);
        }
    }

    // The label keys the pane's SYSTEM rows use, in markup order. Deliberately narrow: it matches only the
    // `<td>@Strings.Get("…")</td>` shape a system row's label cell has, so the pane's many other Strings.Get
    // calls — buttons, tooltips, the mask and sensitivity blocks that sit after these rows — are not swept in.
    private static List<string> LabelKeysInOrder(string markup)
    {
        var known = DocumentDetailRows.InOrder.Select(DocumentDetailRows.LabelKey).ToHashSet(StringComparer.Ordinal);
        return [.. System.Text.RegularExpressions.Regex
            .Matches(markup, @"<td>@Strings\.Get\(""([A-Za-z]+)""\)</td>")
            .Select(m => m.Groups[1].Value)
            .Where(known.Contains)];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
