using SimplArchive.Api.Controllers;
using SimplArchive.Application.Abstractions;
using SimplArchive.Localization;
using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// Which sentence a compare surface shows when the server answers available:false. The old single sentence blamed
// the document's format on an installation without a text-extraction service, and on a check-out with no saved
// working copy — both clients now choose through one function, keyed on the server's unavailableReason.
public class ComparisonUnavailableMessageTests
{
    [Theory]
    [InlineData("no-text-extraction", "CompareNeedsTextExtraction")]
    [InlineData("no-working-copy", "CompareNoWorkingCopy")]
    [InlineData("no-text", "CompareNotAvailable")]
    [InlineData(null, "CompareNotAvailable")]           // an older server sends no reason
    [InlineData("something-new", "CompareNotAvailable")] // an unknown reason degrades to the general sentence
    public void Each_reason_has_its_own_sentence(string? reason, string key)
    {
        Assert.Equal(key, ComparisonUnavailableMessage.KeyOf(reason));
    }

    [Fact]
    public void The_servers_wire_values_are_the_ones_the_clients_key_on()
    {
        // Two literals on two sides of the wire: a renamed server value would silently fall back to the
        // general sentence, which is exactly the bug this replaced.
        Assert.Equal("CompareNeedsTextExtraction", ComparisonUnavailableMessage.KeyOf(ComparisonReason.NoTextExtraction));
        Assert.Equal("CompareNoWorkingCopy", ComparisonUnavailableMessage.KeyOf(ComparisonReason.NoWorkingCopy));
        Assert.Equal(ComparisonReason.NoTextExtraction, ComparisonReason.Of(ComparisonUnavailable.NoTextExtraction));
        Assert.Equal(ComparisonReason.NoText, ComparisonReason.Of(ComparisonUnavailable.NoText));
        Assert.Null(ComparisonReason.Of(ComparisonUnavailable.None));
    }

    [Theory]
    [InlineData("no-text-extraction")]
    [InlineData("no-working-copy")]
    [InlineData("no-text")]
    public void Every_chosen_key_is_translated(string reason)
    {
        var key = ComparisonUnavailableMessage.KeyOf(reason);
        Assert.NotEqual(key, Strings.Get(key)); // a missing key renders as itself
    }
}
