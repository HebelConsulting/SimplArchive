using SimplArchive.Application.Abstractions;

namespace SimplArchive.UnitTests;

/// <summary>
/// Which characters belong to a copied word and which belong to the sentence around it (#788).
/// </summary>
/// <remarks>
/// Pure string work, so it is asserted as a function rather than through a rendered overlay: a UI test here
/// would be slow and would not pin the cases that actually matter, which are the awkward ones below.
/// </remarks>
public class TextLayoutValueTests
{
    [Theory]
    // The reported cases: a word ending a phrase, and one ending a list item.
    [InlineData("Rechnungsnummer:", "Rechnungsnummer")]
    [InlineData("4711,", "4711")]
    [InlineData("Betrag.", "Betrag")]
    // Leading punctuation has the same problem and is handled in the same pass, so it is not reported next week.
    [InlineData("(Anlage", "Anlage")]
    [InlineData("„Vertrag", "Vertrag")]
    [InlineData("(2026)", "2026")]
    // A trailing comma goes even after digits. German decimals make this a real ambiguity, and it is decided
    // rather than guessed: a rule that depended on the token's shape would make two visually similar words copy
    // differently with nothing on screen to say why, and "1.234," is a truncated number either way.
    [InlineData("1.234,", "1.234")]
    // …but punctuation INSIDE the token is part of the value, which is what makes trimming only at the ends
    // safe for the tokens people actually copy.
    [InlineData("1.234,56", "1.234,56")]
    [InlineData("2026-08-27", "2026-08-27")]
    [InlineData("AB-1234/X", "AB-1234/X")]
    [InlineData("-1234", "-1234")]   // a hyphen-minus is not a dash: it opens negative numbers and codes
    [InlineData("invoice_2026", "invoice_2026")]
    // A known and accepted loss: an abbreviation's full stop is indistinguishable from a sentence's.
    [InlineData("Nr.", "Nr")]
    // Nothing but punctuation trims to nothing — the producers drop these rather than drawing a box that
    // copies an empty string.
    [InlineData("—", "")]
    [InlineData("–", "")]
    [InlineData("...", "")]
    [InlineData(".", "")]
    // Whitespace and the ordinary case.
    [InlineData("  spaced  ", "spaced")]
    [InlineData("Vertrag", "Vertrag")]
    [InlineData("", "")]
    public void Trims_what_the_sentence_added_and_keeps_what_the_value_contains(string raw, string expected) =>
        Assert.Equal(expected, TextLayoutValue.Trim(raw));
}
