using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

// A module's pick-then-act surface (core ADR 0786) — its own file, beside DocumentsClient.GenericActions.cs
// and for the same reason: these are one module seam rather than part of the document client's own job, and
// DocumentsClient.cs is at the 1000-line limit this project does not grant itself exceptions to.
public sealed partial class DocumentsClient
{
    /// <summary>
    /// A module's pick-then-act surface on this document (core ADR 0786): what to call it, where the choices
    /// come from, and where the chosen one goes.
    /// </summary>
    /// <remarks>
    /// The client knows no module and no rel — the label and prompt arrive rendered, and the two hrefs are
    /// followed as given. That is what lets a vertical add an action here without a line of client code
    /// (ADR 0737), exactly as the labeled generic actions beside it already do.
    /// </remarks>
    public sealed record ModuleActionInfo(
        string Rel, string Label, string OptionsHref, string CommitHref, string ValueField, string Prompt);

    /// <summary>One choice a module action offers.</summary>
    public sealed record ModuleActionOption(string Value, string Label, string? Detail);

    internal static IReadOnlyList<ModuleActionInfo> ParseModuleActions(JsonElement json)
    {
        if (!json.TryGetProperty("moduleActions", out var actions) || actions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        static string Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? v.GetString() ?? string.Empty : string.Empty;

        return [.. actions.EnumerateArray().Select(a => new ModuleActionInfo(
            Text(a, "rel"), Text(a, "label"), Text(a, "optionsHref"),
            Text(a, "commitHref"), Text(a, "valueField"), Text(a, "prompt")))];
    }

    /// <summary>The choices a module action offers, fetched from the href it named.</summary>
    public async Task<IReadOnlyList<ModuleActionOption>> GetModuleActionOptionsAsync(
        string optionsHref, CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(optionsHref, cancellationToken);

        // The property holding the list is the module's own business, so the FIRST array of objects is
        // taken rather than a name being assumed — a client that required "substitutes" would be a client
        // that knows about flight school.
        foreach (var property in json.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var items = property.Value.EnumerateArray().ToList();
            if (items.Count == 0 || items[0].ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            static string? Field(JsonElement e, params string[] names) =>
                names.Select(n => e.TryGetProperty(n, out var v) ? v.GetString() : null).FirstOrDefault(v => v is not null);

            var options = items
                .Select(i => new ModuleActionOption(
                    Field(i, "value", "email", "id") ?? string.Empty,
                    Field(i, "label", "name") ?? string.Empty,
                    Field(i, "detail")))
                .Where(o => o.Value.Length > 0)
                .ToList();
            if (options.Count > 0)
            {
                return options;
            }
        }

        return [];
    }

    /// <summary>What a module action answered (ABI 1.8, core ADR 0899): a status sentence and, optionally, a value to
    /// show exactly once, with the QR image of its scan address the core drew (ABI 1.13, ADR 0913). All null for a
    /// module that answers nothing.</summary>
    public sealed record ModuleActionOutcome(string? Message, string? RevealLabel, string? RevealValue, string? RevealScanImage = null);

    /// <summary>Commits a module action with the chosen value, and reads its outcome.</summary>
    public async Task<ModuleActionOutcome> InvokeModuleActionAsync(
        string commitHref, string valueField, string value, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, string> { [valueField] = value };
        var response = await _core.Http.PostAsJsonAsync(commitHref, payload, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return await OutcomeAsync(response, cancellationToken);
        }

        // A module's refusal arrives as the same RFC 7807 problem a core refusal does, with a detail its own
        // catalog composed for the request culture (ADR 0767) — the one server-supplied text this client is
        // licensed to show. Swallowing it into a generic failure would cost the pilot the sentence that says
        // what to do about it.
        var (_, _, detail) = await ApiCore.ProblemAsync(response, cancellationToken);
        throw new ApiActionException(detail ?? $"The action could not be completed ({(int)response.StatusCode}).");
    }

    /// <summary>The outcome in a <c>ModuleActionResult</c> body; a body of any other shape is a plain success.</summary>
    public static ModuleActionOutcome ParseModuleActionOutcome(System.Text.Json.JsonElement body)
    {
        if (body.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return new ModuleActionOutcome(null, null, null);
        }

        var message = body.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String ? m.GetString() : null;
        if (body.TryGetProperty("revealOnce", out var reveal) && reveal.ValueKind == System.Text.Json.JsonValueKind.Object
            && reveal.TryGetProperty("value", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            && v.GetString() is { Length: > 0 } value)
        {
            var label = reveal.TryGetProperty("label", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.String ? l.GetString() : null;
            var scanImage = reveal.TryGetProperty("scanImage", out var q) && q.ValueKind == System.Text.Json.JsonValueKind.String ? q.GetString() : null;
            return new ModuleActionOutcome(message, label, value, scanImage);
        }

        return new ModuleActionOutcome(message, null, null);
    }

    /// <summary>
    /// Reads a <c>ModuleActionResult</c> body when there is one; anything else (no body, another shape) is a plain
    /// success, which is what every module answered before ABI 1.8. The revealed value is handed straight to the
    /// caller and kept nowhere else.
    /// </summary>
    private static async Task<ModuleActionOutcome> OutcomeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
        {
            return new ModuleActionOutcome(null, null, null);
        }

        try
        {
            return ParseModuleActionOutcome(await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken));
        }
        catch (System.Text.Json.JsonException)
        {
            return new ModuleActionOutcome(null, null, null);
        }
    }
}
