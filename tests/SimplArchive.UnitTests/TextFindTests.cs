using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

/// <summary>
/// The shared text-find arithmetic (#1063, ADR 0651): both previews answer "which offsets match, which one
/// is active, how does the text split for rendering" from this one implementation — these tests pin the
/// answers themselves; each client's state tests pin only its delegation.
/// </summary>
public class TextFindTests
{
    [Fact]
    public void Matching_is_case_insensitive_and_non_overlapping()
    {
        var matches = TextFind.Matches("LSZH lszh LsZh", "lszh");

        Assert.Equal([0, 5, 10], matches);
        Assert.Equal([0], TextFind.Matches("aaa", "aa")); // the scan resumes AFTER a match: one hit, not two
    }

    [Fact]
    public void An_empty_text_or_query_matches_nothing()
    {
        Assert.Empty(TextFind.Matches(null, "x"));
        Assert.Empty(TextFind.Matches("abc", null));
        Assert.Empty(TextFind.Matches("abc", string.Empty));
    }

    [Fact]
    public void A_degenerate_query_is_capped()
    {
        Assert.Equal(TextFind.MaxMatches, TextFind.Matches(new string('a', TextFind.MaxMatches * 3), "a").Count);
    }

    [Fact]
    public void Cycling_wraps_in_both_directions_and_zero_stays_zero()
    {
        Assert.Equal(2, TextFind.Cycle(1, 3, +1));
        Assert.Equal(1, TextFind.Cycle(3, 3, +1));
        Assert.Equal(3, TextFind.Cycle(1, 3, -1));
        Assert.Equal(0, TextFind.Cycle(0, 0, +1));
    }

    [Fact]
    public void Segments_split_around_the_matches_and_flag_the_active_one()
    {
        const string text = "to LSZH via lszh";
        var matches = TextFind.Matches(text, "lszh");

        var segments = TextFind.Segments(text, matches, "lszh".Length, activeIndex: 2).ToList();

        Assert.Equal([("to ", false, false), ("LSZH", true, false), (" via ", false, false), ("lszh", true, true)], segments);
        Assert.Equal(text, string.Concat(segments.Select(s => s.Segment))); // nothing lost, nothing doubled
    }

    [Fact]
    public void No_matches_yields_the_whole_text_as_one_plain_segment()
    {
        Assert.Equal([("abc", false, false)], TextFind.Segments("abc", [], 1, 0).ToList());
        Assert.Empty(TextFind.Segments(null, [], 1, 0));
    }
}
