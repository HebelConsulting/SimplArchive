using System.Text.Json;
using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// Writing a certificate's <c>Revoked on</c> field — shared by the bulk import and the single revoke.
/// </summary>
/// <remarks>
/// ONE implementation, because two would drift and the divergence would be invisible: a revocation that
/// wrote the field one way in bulk and another way singly would read as working in both until somebody
/// compared them. The same reasoning the enrolment projection gives for sharing its derivation with the
/// rebuild command.
/// </remarks>
internal static class CertificateRevocation
{
    /// <summary>
    /// Writes the <c>Revoked on</c> index field — the module's only revocation interface (its ADR 0005).
    /// </summary>
    /// <remarks>
    /// Four hops, and each is forced rather than chosen. The document is read for its ETag and its rels; the
    /// MASK is read because an unset field has no value row and therefore does not appear in the index data
    /// at all, so its field-definition id can only come from the mask; the index data is read because the PUT
    /// REPLACES the whole set (there are deliberately no per-field endpoints), so everything else has to be
    /// sent back unchanged; and the PUT carries the ETag the document was read with, which is the precondition
    /// a concurrent edit is supposed to fail against.
    /// </remarks>
    internal static async Task SetRevokedOnAsync(
        SimplArchiveApi api,
        Hypermedia hypermedia,
        Guid documentId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        var template = await hypermedia.RootHrefAsync("document", cancellationToken);
        var documentHref = template.Replace("{id}", documentId.ToString("D"), StringComparison.Ordinal);

        // Read ONCE for the tag AND both rels (ADR 0557: one read, many follows). Reading it a second time
        // for the links would be the shape that ADR names — one request turned into two to learn something
        // already in hand.
        var (document, etag) = await api.GetWithETagAsync(documentHref, cancellationToken);
        var links = Hypermedia.LinksOf(document);

        var maskHref = Hypermedia.Href(links, "mask", "The certificate document");
        var indexHref = Hypermedia.Href(links, "index-data", "The certificate document");

        var revokedOnId = FieldDefinitionId(await api.GetAsync(maskHref, cancellationToken), "Revoked on");
        var fields = CurrentFields(await api.GetAsync(indexHref, cancellationToken));
        fields[revokedOnId] = [revokedAt.UtcDateTime.ToString("yyyy-MM-dd")];

        await api.PutWithETagAsync(
            indexHref,
            new { fields = fields.Select(f => new { fieldDefinitionId = f.Key, values = f.Value }).ToList() },
            etag,
            cancellationToken);
    }

    private static Guid FieldDefinitionId(JsonElement mask, string fieldName)
    {
        if (mask.TryGetProperty("fields", out var fields))
        {
            foreach (var field in fields.EnumerateArray())
            {
                if (field.TryGetProperty("name", out var name)
                    && string.Equals(name.GetString(), fieldName, StringComparison.Ordinal)
                    && field.TryGetProperty("id", out var id))
                {
                    return id.GetGuid();
                }
            }
        }

        throw new CliException(
            $"The certificate document's mask has no '{fieldName}' field, so there is nothing to write. "
            + "The installation may predate it — a restart heals the well-known masks.");
    }

    private static Dictionary<Guid, List<string>> CurrentFields(JsonElement indexData)
    {
        var fields = new Dictionary<Guid, List<string>>();
        if (!indexData.TryGetProperty("fields", out var groups))
        {
            return fields;
        }

        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("fieldDefinitionId", out var id))
            {
                continue;
            }

            var values = group.TryGetProperty("values", out var v)
                ? v.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList()
                : [];

            fields[id.GetGuid()] = values;
        }

        return fields;
    }
}
