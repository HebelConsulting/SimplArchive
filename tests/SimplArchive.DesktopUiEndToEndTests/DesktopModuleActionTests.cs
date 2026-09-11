using System.Text.Json;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// A module's pick-then-act surface in this client (core ADR 0786): parsed from the document resource,
// rendered as a labeled button, committed to the href the action named.
//
// View-model level, deliberately, and the screenshot attempt is why. The button sits below the detail pane's
// fold — a height the workbench splitter owns — so a headless capture shows neither it NOR the labeled
// generic actions beside it, which was confirmed by injecting one of those as a control and finding it
// equally absent. A picture cannot verify this surface; what it renders FROM can.
//
// The property under test throughout is that this client knows no module: it never names a rel, never reads
// a module-specific field, and would render a second module's action with no change at all.
[Collection("DesktopConfig")]
public class DesktopModuleActionTests
{
    private static JsonElement Document(string moduleActions) =>
        JsonSerializer.Deserialize<JsonElement>($$"""
        {
          "id": "6f1b7e64-0000-0000-0000-000000000001",
          "name": "Training flight",
          "links": [],
          "moduleActions": {{moduleActions}}
        }
        """);

    [Fact]
    public void An_action_is_read_from_the_document_resource()
    {
        var json = Document("""
        [{
          "rel": "flight-school:hand-over",
          "label": "Hand over to…",
          "optionsHref": "/api/modules/flight-school/bookings/1/substitutes",
          "commitHref": "/api/modules/flight-school/bookings/1/instructor",
          "valueField": "email",
          "prompt": "Who takes this flight over?"
        }]
        """);

        var action = Assert.Single(DocumentsClient.ParseModuleActions(json));

        Assert.Equal("flight-school:hand-over", action.Rel);
        Assert.Equal("Hand over to…", action.Label);
        Assert.Equal("email", action.ValueField);
        Assert.Equal("Who takes this flight over?", action.Prompt);
        Assert.EndsWith("/substitutes", action.OptionsHref, StringComparison.Ordinal);
        Assert.EndsWith("/instructor", action.CommitHref, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_offering_nothing_yields_nothing()
    {
        // The overwhelmingly common case — every document in a tenant with no module, and every document a
        // module has no business with. Absence is the normal answer, not an error (ADR 0543).
        Assert.Empty(DocumentsClient.ParseModuleActions(Document("[]")));
        Assert.Empty(DocumentsClient.ParseModuleActions(
            JsonSerializer.Deserialize<JsonElement>("""{"id":"x","name":"y"}""")));
    }

    [Fact]
    public void The_pane_shows_the_actions_and_clears_them_when_the_subject_changes()
    {
        var vm = new MainWindowViewModel();
        var action = new DocumentsClient.ModuleActionInfo(
            "flight-school:hand-over", "Hand over to…", "/options", "/commit", "email", "Who?");

        vm.SetDetailModuleActions([action]);
        Assert.True(vm.HasDetailModuleActions);
        Assert.Equal("Hand over to…", Assert.Single(vm.DetailModuleActions).Label);

        // CLEARED when the subject changes (ADR 0559): an action inherited from the previously selected
        // document would fetch its choices for one document and commit them against another.
        vm.SetDetailModuleActions(null);
        Assert.False(vm.HasDetailModuleActions);
        Assert.Empty(vm.DetailModuleActions);
    }

    [Fact]
    public void The_picker_offers_every_choice_with_the_modules_own_reason()
    {
        var action = new DocumentsClient.ModuleActionInfo(
            "flight-school:hand-over", "Hand over to…", "/options", "/commit", "email", "Who takes it over?");
        var picker = new ModuleActionPickerViewModel(action,
        [
            new DocumentsClient.ModuleActionOption("anna@school.test", "Anna Current", "FI(A) · offered and free"),
            new DocumentsClient.ModuleActionOption("nils@school.test", "Nils Night", null),
        ]);

        Assert.True(picker.HasOptions);
        Assert.Equal("Who takes it over?", picker.Prompt);

        // The detail line is the MODULE's sentence, shown verbatim: only the module knows what distinguishes
        // its candidates, and a picker whose entries cannot be told apart is one nobody uses with confidence.
        Assert.Equal("FI(A) · offered and free", picker.Options[0].Detail);
        Assert.True(picker.Options[0].HasDetail);
        Assert.False(picker.Options[1].HasDetail);

        // Preselecting the first choice means Enter commits the obvious one.
        Assert.Equal("anna@school.test", picker.Selected?.Value);
    }

    [Fact]
    public void An_empty_picker_says_so_rather_than_showing_an_empty_pane()
    {
        var action = new DocumentsClient.ModuleActionInfo("x:y", "Do it", "/options", "/commit", "v", "Which?");
        var picker = new ModuleActionPickerViewModel(action, []);

        // Rare, because the server offers the action only when it can be served — but a dialog that simply
        // looks broken is worse than one that explains itself.
        Assert.False(picker.HasOptions);
        Assert.True(picker.HasNoOptions);
        Assert.Null(picker.Selected);
    }
}
