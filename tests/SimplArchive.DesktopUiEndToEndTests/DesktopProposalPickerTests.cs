using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The machine-proposal picker on a field editor (ABI 0.11, ADR 0769): a proposal that fills a field puts
// a flyout beside that field's editor; choosing a candidate writes its VALUE into the editor — the user
// still saves. Pure VM: the wire half is pinned by the core e2e (TestModule proposal), and the visual half
// by live verification against the flight-school module (the web-UI stack boots no module — ADR 0769).
public class DesktopProposalPickerTests
{
    private static MaskFieldEditViewModel Field(string name) =>
        MaskFieldEditViewModel.Create(new MasksClient.MaskFieldInfo(Guid.NewGuid(), name, "EmailAddress", false), []);

    [Fact]
    public void Offering_proposals_arms_the_picker_and_applying_writes_the_value()
    {
        var field = Field("Instructor");
        Assert.False(field.HasProposals);

        field.OfferProposals("Propose instructor",
        [
            ("anna@school.example", "Anna Current", "Examiner certificate valid to 2027-06-01"),
            ("nils@school.example", "Nils Night", null),
        ]);

        Assert.True(field.HasProposals);
        Assert.Equal("Propose instructor", field.ProposalLabel);
        Assert.Equal("Anna Current — Examiner certificate valid to 2027-06-01", field.Proposals[0].Display);
        Assert.Equal("Nils Night", field.Proposals[1].Display); // no detail, no dangling dash

        field.Proposals[0].ApplyCommand.Execute(null);
        Assert.Equal("anna@school.example", field.TextValue); // typing stays possible; the pick just typed for you
    }

    [Fact]
    public void An_empty_answer_keeps_the_picker_hidden()
    {
        var field = Field("Instructor");
        field.OfferProposals("Propose instructor", []);

        Assert.False(field.HasProposals);
    }
}
