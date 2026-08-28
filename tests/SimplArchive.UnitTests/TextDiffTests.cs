using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The shared side-by-side diff (#803, ADR 0712): both clients render THESE rows, so the rows themselves are
// what gets pinned — alignment, kinds, line numbers, and where the word-level emphasis lands.
public class TextDiffTests
{
    [Fact]
    public void Identical_texts_are_all_unchanged_rows()
    {
        var rows = TextDiff.Compute("one\ntwo", "one\ntwo");

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(DiffRowKind.Unchanged, r.Kind));
        Assert.Equal(1, rows[0].Old!.LineNumber);
        Assert.Equal(1, rows[0].New!.LineNumber);
    }

    [Fact]
    public void A_changed_line_pairs_old_and_new_with_word_emphasis()
    {
        var rows = TextDiff.Compute("the quick brown fox", "the quick red fox");

        var row = Assert.Single(rows);
        Assert.Equal(DiffRowKind.Changed, row.Kind);

        // Only the changed word carries emphasis — "the quick " and " fox" stay plain on both sides.
        Assert.Equal("brown", string.Concat(row.Old!.Segments.Where(s => s.Emphasized).Select(s => s.Text)));
        Assert.Equal("red", string.Concat(row.New!.Segments.Where(s => s.Emphasized).Select(s => s.Text)));
        Assert.Equal("the quick brown fox", string.Concat(row.Old.Segments.Select(s => s.Text)));
        Assert.Equal("the quick red fox", string.Concat(row.New.Segments.Select(s => s.Text)));
    }

    [Fact]
    public void An_inserted_line_is_an_added_row_with_no_old_cell()
    {
        var rows = TextDiff.Compute("one\nthree", "one\ntwo\nthree");

        Assert.Equal(3, rows.Count);
        Assert.Equal(DiffRowKind.Unchanged, rows[0].Kind);
        Assert.Equal(DiffRowKind.Added, rows[1].Kind);
        Assert.Null(rows[1].Old);
        Assert.Equal(2, rows[1].New!.LineNumber);
        Assert.Equal(DiffRowKind.Unchanged, rows[2].Kind);
        Assert.Equal(2, rows[2].Old!.LineNumber);
        Assert.Equal(3, rows[2].New!.LineNumber);
    }

    [Fact]
    public void A_deleted_line_is_a_removed_row_with_no_new_cell()
    {
        var rows = TextDiff.Compute("one\ntwo\nthree", "one\nthree");

        Assert.Equal(3, rows.Count);
        Assert.Equal(DiffRowKind.Removed, rows[1].Kind);
        Assert.Null(rows[1].New);
        Assert.Equal(2, rows[1].Old!.LineNumber);
    }

    [Fact]
    public void Unequal_change_runs_pair_what_they_can_and_overhang_stays_pure()
    {
        // Two old lines become three new ones: two Changed pairs, then one Added.
        var rows = TextDiff.Compute("a\nx1\nx2\nz", "a\ny1\ny2\ny3\nz");

        Assert.Equal(
            new[] { DiffRowKind.Unchanged, DiffRowKind.Changed, DiffRowKind.Changed, DiffRowKind.Added, DiffRowKind.Unchanged },
            rows.Select(r => r.Kind).ToArray());
    }

    [Fact]
    public void Line_endings_never_produce_phantom_changes()
    {
        var rows = TextDiff.Compute("one\r\ntwo\r\n", "one\ntwo\n");

        Assert.All(rows, r => Assert.Equal(DiffRowKind.Unchanged, r.Kind));
    }

    [Fact]
    public void A_wholly_new_text_is_all_added_rows()
    {
        var rows = TextDiff.Compute(string.Empty, "one\ntwo");

        // The empty side is one empty line; it pairs with the first new line as Changed, the rest are Added.
        Assert.Contains(rows, r => r.Kind is DiffRowKind.Added or DiffRowKind.Changed);
        Assert.DoesNotContain(rows, r => r.Kind == DiffRowKind.Unchanged && r.Old!.Segments[0].Text.Length > 0);
        Assert.Equal("one\ntwo", string.Join("\n", rows.Where(r => r.New is not null).Select(r => string.Concat(r.New!.Segments.Select(s => s.Text)))));
    }

    [Fact]
    public void Both_sides_reassemble_verbatim()
    {
        // Whatever the alignment does, concatenating each side's cells must reproduce that side's text.
        const string oldText = "alpha\nbeta gamma\ndelta\n\nepsilon";
        const string newText = "alpha\nbeta GAMMA extra\n\nzeta\nepsilon tail";
        var rows = TextDiff.Compute(oldText, newText);

        Assert.Equal(oldText.Split('\n'), rows.Where(r => r.Old is not null).Select(r => string.Concat(r.Old!.Segments.Select(s => s.Text))));
        Assert.Equal(newText.Split('\n'), rows.Where(r => r.New is not null).Select(r => string.Concat(r.New!.Segments.Select(s => s.Text))));
    }
}
