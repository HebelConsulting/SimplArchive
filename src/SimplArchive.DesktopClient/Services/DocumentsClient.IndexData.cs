using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

// Reading a document's index data, split from the main DocumentsClient file by responsibility (the
// 1000-line rule): the served field groups, and the DocumentReference targets the server resolved for them
// (ADR 0773).
public sealed partial class DocumentsClient
{
    public sealed record IndexField(string FieldName, IReadOnlyList<string> Values, string DataType = "Text")
    {
        /// <summary>A DocumentReference field's resolved targets (ADR 0773), index-aligned with
        /// <see cref="Values"/> — server-resolved, so the pane neither fetches per value nor composes an
        /// address (ADRs 0543/0557). Empty for every other field type.</summary>
        public IReadOnlyList<IndexFieldTarget> Targets { get; init; } = [];
    }

    /// <summary>One resolved target of a DocumentReference value. No name and no links means the server
    /// says it is not available to this caller — which the pane draws as such, never as a bare id.</summary>
    public sealed record IndexFieldTarget(Guid Id, string? Name, IReadOnlyList<IndexFieldLink> Links)
    {
        public bool CanOpen => Links.Any(l => l.Rel == "document");
    }

    /// <summary>One advertised address on a target — its presence is the server saying "available to you,
    /// here, now" (ADR 0543), which is what the pane gates the affordance on.</summary>
    public sealed record IndexFieldLink(string Rel, string Href);

    public async Task<List<IndexField>> GetIndexDataAsync(string indexDataHref, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.GetFromJsonAsync<JsonElement>(indexDataHref, cancellationToken);
        var fields = new List<IndexField>();
        if (response.TryGetProperty("fields", out var items))
        {
            foreach (var field in items.EnumerateArray())
            {
                var values = field.TryGetProperty("values", out var vs) && vs.ValueKind == JsonValueKind.Array
                    ? vs.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                    : [];
                fields.Add(new IndexField(
                    field.GetProperty("fieldName").GetString() ?? "",
                    values,
                    field.TryGetProperty("dataType", out var dt) ? dt.GetString() ?? "Text" : "Text")
                {
                    Targets = ParseTargets(field),
                });
            }
        }

        return fields;
    }

    /// <summary>The resolved targets of a DocumentReference field (ADR 0773), in wire order so they stay
    /// index-aligned with the values.</summary>
    private static List<IndexFieldTarget> ParseTargets(JsonElement field)
    {
        if (!field.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return targets.EnumerateArray().Select(t => new IndexFieldTarget(
            t.TryGetProperty("id", out var id) ? id.GetGuid() : Guid.Empty,
            t.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
            t.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array
                ? links.EnumerateArray().Select(l => new IndexFieldLink(
                    l.TryGetProperty("rel", out var rel) ? rel.GetString() ?? "" : "",
                    l.TryGetProperty("href", out var href) ? href.GetString() ?? "" : "")).ToList()
                : [])).ToList();
    }
}
