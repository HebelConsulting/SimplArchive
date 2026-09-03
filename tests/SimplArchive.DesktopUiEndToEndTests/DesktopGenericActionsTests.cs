using System.Text.Json;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopUiEndToEndTests;

// The generic action surface's parse (ADR 0743): a link carrying a LABEL and a non-GET method is an action
// the server wants rendered; everything else stays the navigation machinery the rel-map serves. The label
// being the signal is the whole contract — these tests pin its two halves, because a widened parse would
// re-render machinery the client already draws its own affordances for, and a narrowed one would silently
// hide a module's actions (the safe direction, and therefore the one nobody reports).
public class DesktopGenericActionsTests
{
    private static JsonElement Resource(params object[] links) =>
        JsonSerializer.SerializeToElement(new { links });

    [Fact]
    public void A_labeled_non_get_link_is_an_action()
    {
        var actions = DocumentsClient.ParseGenericActions(Resource(
            new { rel = "accept-aircraft", href = "/api/modules/fs/charters/1/accept", method = "POST", label = "Accept aircraft" }));

        var action = Assert.Single(actions);
        Assert.Equal("accept-aircraft", action.Rel);
        Assert.Equal("Accept aircraft", action.Label);
        Assert.Equal("POST", action.Method);
        // Relative, like every followed href — the HttpClient carries the base address.
        Assert.Equal("api/modules/fs/charters/1/accept", action.Href);
    }

    [Fact]
    public void Unlabeled_links_and_labeled_GETs_are_not_actions()
    {
        var actions = DocumentsClient.ParseGenericActions(Resource(
            // The machinery a client navigates by — never rendered generically.
            new { rel = "self", href = "/api/documents/1", method = "GET" },
            new { rel = "delete", href = "/api/documents/1", method = "DELETE" },
            // Labeled but a GET: a read is not an action, whatever the server captioned it.
            new { rel = "report", href = "/api/documents/1/report", method = "GET", label = "Report" }));

        Assert.Empty(actions);
    }

    [Fact]
    public void A_resource_without_links_yields_no_actions()
    {
        Assert.Empty(DocumentsClient.ParseGenericActions(JsonSerializer.SerializeToElement(new { name = "x" })));
    }
}
