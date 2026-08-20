using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The letters a contact is drawn with when it has no picture. Shared by both clients, so it is tested once —
// and the cases worth testing are all the ones that are not "Ada Lovelace", because those are where two
// independent implementations would have quietly disagreed.
public class ContactInitialsTests
{
    [Theory]
    [InlineData("Ada Lovelace", "AL")]
    // First and LAST: a middle name is the part a reader skips, and the surname is what identifies the person.
    [InlineData("Jean-Paul van der Berg", "JB")]
    // One word gives two letters rather than one — a single "T" identifies far too many people.
    [InlineData("Prince", "PR")]
    [InlineData("X", "X")]
    // Organisation-only cards are ordinary; they arrive by sync and must not draw as a rendering fault.
    [InlineData("Tuba Skinny", "TS")]
    [InlineData("  Lluís   Coloma  ", "LC")]
    public void A_name_gives_one_or_two_letters(string name, string expected) =>
        Assert.Equal(expected, ContactInitials.From(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Punctuation is skipped rather than drawn: an avatar showing "(" reads as a bug, not as a name.
    [InlineData("()")]
    [InlineData("@")]
    public void A_card_with_no_usable_name_is_drawn_as_unknown(string? name) =>
        Assert.Equal(ContactInitials.Unknown, ContactInitials.From(name));

    [Fact]
    public void Punctuation_around_a_real_name_does_not_replace_it()
    {
        Assert.Equal("AL", ContactInitials.From("(Ada) Lovelace"));
        Assert.Equal("AL", ContactInitials.From("@ada lovelace"));
    }

    [Fact]
    public void A_letter_outside_the_latin_alphabet_survives_whole()
    {
        // Enumerated by text element rather than by char: half a surrogate pair renders as a replacement glyph,
        // and a combining accent detached from its letter is worse than no avatar at all.
        Assert.Equal("ÉT", ContactInitials.From("Élise Traoré"));
        Assert.Equal("ЛТ", ContactInitials.From("Лев Толстой"));
        Assert.Equal("大山", ContactInitials.From("大 山"));
    }

    [Fact]
    public void The_upper_casing_is_invariant_so_two_readers_see_the_same_avatar()
    {
        // Turkish lower-case i upper-cases to a dotted İ under a Turkish culture. The avatar identifies a
        // contact, so it must not depend on who is looking at it.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Assert.Equal("IK", ContactInitials.From("irem kaya"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }
}
