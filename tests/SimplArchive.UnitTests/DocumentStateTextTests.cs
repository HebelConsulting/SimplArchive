using SimplArchive.Localization;
using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The state texts the desktop pane and the phone's details share: every key exists, and an unknown state is shown raw.
public sealed class DocumentStateTextTests
{
    [Theory]
    [InlineData(null, "WfNotStarted")]
    [InlineData("InReview", "WfStateInReview")]
    [InlineData("Released", "WfStateReleased")]
    [InlineData("SomethingNew", null)]
    public void A_workflow_state_maps_to_its_text_and_an_unknown_one_to_none(string? status, string? key) =>
        Assert.Equal(key, DocumentStateText.WorkflowKey(status));

    [Theory]
    [InlineData("ConvertibleScan", "OcrVerdictConvertibleScan")]
    [InlineData(null, null)]
    public void An_ocr_verdict_maps_to_its_text_and_no_verdict_to_none(string? verdict, string? key) =>
        Assert.Equal(key, DocumentStateText.OcrVerdictKey(verdict));

    [Fact]
    public void Every_key_has_a_text()
    {
        foreach (var key in new[] { "WfNotStarted", "WfStateDraft", "WfStateInReview", "WfStateApproved", "WfStateRejected", "WfStateReleased",
                     "OcrVerdictConvertibleScan", "OcrVerdictNotAScan", "OcrVerdictUnreadable" })
        {
            Assert.NotEqual(key, Strings.Get(key));
        }
    }
}
