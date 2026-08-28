using System;
using System.Text.Json;
using System.Threading.Tasks;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Deep links on the desktop (#761): "Copy link" produces the https web-app URL (universally openable —
// what circulates never depends on the recipient's client), and GoToDeepLinkAsync lands a pasted or
// scheme-launched link the way the Search tab's "Go to" lands a hit: containing folder open, object
// selected, detail loaded. Resolution follows the API root's TEMPLATED `document` rel and the resource's
// `parent` rel — the id is the link's whole payload, and the template is the server handing out the shape.
public partial class MainWindowViewModel
{
    /// <summary>The link for a node — the web app's /go route on this client's server.</summary>
    public static string DeepLinkFor(NodeViewModel node) =>
        DeepLinks.BuildLink(DesktopClientOptions.ApiBaseUrl, node.Id);

    /// <summary>A scheme-launched link parked by Program.Main before login; consumed after the workbench loads.</summary>
    public static string? PendingDeepLink { get; set; }

    /// <summary>Opens a pasted or scheme-launched deep link. Returns false (with the status line saying why)
    /// when the text is not a link or the document is not reachable for this user.</summary>
    public async Task<bool> GoToDeepLinkAsync(string? text)
    {
        if (_api is null)
        {
            return false;
        }

        if (DeepLinks.ParseDocumentId(text) is not { } id)
        {
            Status = Strings.Get("DeepLinkInvalid");
            return false;
        }

        var template = await _api.Core.RootHrefAsync("document");
        using var response = await _api.Core.Http.GetAsync(template.Replace("{id}", id.ToString()));
        if (!response.IsSuccessStatusCode)
        {
            Status = Strings.Get("DeepLinkGone");
            return false;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var self = ApiCore.RelHref(doc.RootElement, "self")
            ?? throw new InvalidOperationException("The document resource advertised no 'self' rel (ADR 0543).");
        var parent = ApiCore.RelHref(doc.RootElement, "parent");

        SelectedTab = 0;
        if (parent is not null)
        {
            await RevealDocumentInTreeAsync(id, self, parent);
        }
        else
        {
            await RevealFolderInTreeAsync(self);
        }

        return true;
    }
}
