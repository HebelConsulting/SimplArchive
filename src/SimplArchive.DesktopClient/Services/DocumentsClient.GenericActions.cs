using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

// The generic action surface (ADR 0743), split from the main DocumentsClient file by responsibility (the
// 1000-line rule): parsing which links are actions, and executing one.
public sealed partial class DocumentsClient
{
    /// <summary>
    /// One entry of the generic action surface (ADR 0743): a link the server labeled, meaning "render me as
    /// an action" — the client needs no knowledge of the rel. Unlabeled links stay navigation machinery.
    /// </summary>
    public sealed record GenericActionInfo(string Rel, string Label, string Method, string Href);

    // The generic action surface's parse (ADR 0743): a link carrying a LABEL and a non-GET method is an
    // action the server wants rendered; everything else is the navigation machinery the rel-map above
    // already serves. The label being the signal is what spares the client a known-rel list that would
    // drift the moment a module ships a rel this build has never heard of.
    internal static IReadOnlyList<GenericActionInfo> ParseGenericActions(JsonElement json)
    {
        if (!json.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var actions = new List<GenericActionInfo>();
        foreach (var l in links.EnumerateArray())
        {
            if (l.TryGetProperty("label", out var label) && label.GetString() is { Length: > 0 } caption
                && l.TryGetProperty("method", out var method) && method.GetString() is { Length: > 0 } m
                && !string.Equals(m, "GET", StringComparison.OrdinalIgnoreCase)
                && l.TryGetProperty("rel", out var rel) && rel.GetString() is { Length: > 0 } r
                && l.TryGetProperty("href", out var href) && href.GetString() is { Length: > 0 } h)
            {
                actions.Add(new GenericActionInfo(r, caption, m.ToUpperInvariant(), h.TrimStart('/')));
            }
        }

        return actions;
    }

    /// <summary>
    /// Executes a generic action: the labeled link's method against its advertised href, no payload —
    /// the parameterless-transition shape ADR 0743 scopes the surface to. A refusal surfaces the problem
    /// document's detail, which since ADR 0742 carries the explanation a user can act on.
    /// </summary>
    public async Task ExecuteActionAsync(GenericActionInfo action, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(new HttpMethod(action.Method), action.Href);
        var response = await _core.Http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // Parse ONCE, branch from the parse (the read-the-problem-body-once lesson).
        // Mapped through ApiErrorText, never the English `detail` (issue #424); an unmapped module code
        // falls back to the generic localized sentence until ADR 0742's engine ships server-localized
        // explanations as their own field.
        throw new ApiActionException(SimplArchive.Localization.ApiErrorText.For(await ApiCore.ErrorCodeAsync(response, cancellationToken)));
    }
}
