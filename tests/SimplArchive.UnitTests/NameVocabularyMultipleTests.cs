using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

/// <summary>
/// A mask whose NAME is a list completes the value being typed and leaves the chosen ones alone (ABI 0.23).
/// </summary>
/// <remarks>
/// <para>
/// A NOTAM briefing folder named "LSZH LSAS EDGG" briefs a whole route in one digest, because the provider's
/// query takes an array and answers the union. Completing the whole box would replace the route with its
/// last code — so the suggestion's VALUE is the whole resulting string while its LABEL stays the bare code.
/// </para>
/// <para>
/// Tested against the SHARED rule rather than a copy of it. The first version of this file reimplemented the
/// split inside the test, which proves the test agrees with itself and nothing about the clients.
/// </para>
/// </remarks>
public class NameVocabularyMultipleTests
{
    [Theory]
    [InlineData("LSA", "", "LSA")]                       // the first code
    [InlineData("LSZH LSA", "LSZH ", "LSA")]             // the second, with the first preserved
    [InlineData("LSZH LSAS ED", "LSZH LSAS ", "ED")]     // the third
    [InlineData("LSZH ", "LSZH ", "")]                   // a trailing space starts a NEW code
    public void Only_the_value_being_typed_is_completed(string typed, string prefix, string fragment)
    {
        Assert.Equal((prefix, fragment), NameVocabularyInput.SplitTrailingValue(typed, multiple: true));
    }

    [Fact]
    public void A_single_value_name_completes_the_whole_box()
    {
        // METAR/TAF and Aerodrome accept exactly ONE code — their handlers read the whole folder name as the
        // identifier — so the box must not quietly invite a second that they would then ignore.
        Assert.Equal((string.Empty, "LSZH LSA"), NameVocabularyInput.SplitTrailingValue("LSZH LSA", multiple: false));
    }

    [Fact]
    public void A_trailing_space_opens_the_vocabulary_for_the_next_value()
    {
        // An empty fragment yields the vocabulary's first page rather than nothing — the affordance that
        // tells a user another code may follow. Silence there would read as "this name is finished".
        Assert.Empty(NameVocabularyInput.SplitTrailingValue("LSZH ", multiple: true).Fragment);
    }

    [Theory]
    [InlineData("LSZH ", "LSAS", true, "LSZH LSAS ")]    // spliced, caret ready for the next
    [InlineData("", "LSAS", true, "LSAS ")]
    [InlineData("", "LSAS", false, "LSAS")]              // single-value: no trailing space to invite another
    public void Choosing_a_value_yields_the_whole_resulting_name(
        string prefix, string value, bool multiple, string expected)
    {
        // Both completion controls REPLACE their content with the chosen item, so each suggestion must carry
        // the whole answer. Handing over the bare code is what would turn "LSZH LSA" into "LSAS".
        Assert.Equal(expected, NameVocabularyInput.Compose(prefix, value, multiple));
    }

    [Fact]
    public void Null_input_is_the_empty_first_value()
    {
        Assert.Equal((string.Empty, string.Empty), NameVocabularyInput.SplitTrailingValue(null, multiple: true));
    }
}
