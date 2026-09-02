using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Imap;

/// <summary>One index field as the detail pane shows it: the field's name, and its value(s) in list order.</summary>
internal sealed record ImapIndexField(string Name, string Value);

/// <summary>
/// What the clients' detail pane shows about a document, for the synthetic message a NON-email document is
/// served as (#562). A real <c>.eml</c> is returned byte-for-byte and never carries this.
/// </summary>
internal sealed record ImapMessageDetails(
    DateTimeOffset Filed,
    DateOnly DocumentDate,
    string? CreatedBy,
    int? VersionNumber,
    int VersionCount,
    long? SizeBytes,
    string? SensitivityLabel,
    string? OcrLanguages,
    string? MaskName,
    IReadOnlyList<ImapIndexField> IndexFields);

/// <summary>
/// Loads <see cref="ImapMessageDetails"/> for a whole mailbox at once.
/// </summary>
/// <remarks>
/// <para>
/// BATCHED, not per message. A mailbox listing already walks its documents, and adding a per-document lookup
/// for the creator, the mask, the label and the index values would be five more round trips per row on the
/// path a mail client hits every time it opens a folder. Each lookup here is one query over the whole page.
/// </para>
/// <para>
/// It lives in its own file rather than in <c>ImapMailboxes</c>, which is already 905 lines: CLAUDE.md asks
/// that a class be split by responsibility as it approaches the limit rather than after crossing it.
/// </para>
/// </remarks>
internal static class ImapMessageDetailsLoader
{
    internal static async Task<IReadOnlyDictionary<Guid, ImapMessageDetails>> LoadAsync(
        SimplArchiveDbContext db,
        IReadOnlyList<Document> documents,
        IReadOnlyDictionary<Guid, DocumentVersion> currentVersions)
    {
        if (documents.Count == 0)
        {
            return new Dictionary<Guid, ImapMessageDetails>();
        }

        var documentIds = documents.Select(d => d.Id).ToList();
        var maskVersionIds = documents.Where(d => d.MaskVersionId is not null)
            .Select(d => d.MaskVersionId!.Value).Distinct().ToList();
        var labelIds = documents.Where(d => d.SensitivityLabelId is not null)
            .Select(d => d.SensitivityLabelId!.Value).Distinct().ToList();

        var userIds = currentVersions.Values.Where(v => v.CreatedByUserId is not null)
            .Select(v => v.CreatedByUserId!.Value).Distinct().ToList();
        var serviceAccountIds = currentVersions.Values.Where(v => v.CreatedByServiceAccountId is not null)
            .Select(v => v.CreatedByServiceAccountId!.Value).Distinct().ToList();

        var users = await db.Users.IgnoreQueryFilters().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var serviceAccounts = await db.ServiceAccounts.IgnoreQueryFilters()
            .Where(s => serviceAccountIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
        var maskNames = await db.MaskVersions.IgnoreQueryFilters()
            .Where(m => maskVersionIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name);
        var labelNames = await db.SensitivityLabelDefinitions.IgnoreQueryFilters()
            .Where(l => labelIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name);

        var versionCounts = await db.DocumentVersions.IgnoreQueryFilters()
            .Where(v => documentIds.Contains(v.DocumentId) && v.Status == DocumentVersionStatus.Confirmed)
            .GroupBy(v => v.DocumentId)
            .Select(g => new { DocumentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.DocumentId, x => x.Count);

        // The values, joined to their definitions so each one is read BY NAME. Selecting from FieldValues on
        // DocumentId alone returns an arbitrary one of a document's rows, which is the defect
        // DocumentFieldQueries exists to warn about.
        //
        // Ordered by the definition's CreatedAt then Name, then the value's Ordinal. The model carries no
        // display order, and an UNORDERED body would be a real defect rather than a cosmetic one: the same
        // message would serialise differently between two fetches, so a client's cached copy and the server's
        // next answer would disagree, and SEARCH — which scans these very bytes — would match inconsistently.
        var fieldRows = await db.FieldValues.IgnoreQueryFilters()
            .Where(v => documentIds.Contains(v.DocumentId))
            .Join(
                db.FieldDefinitions.IgnoreQueryFilters(),
                value => value.FieldDefinitionId,
                definition => definition.Id,
                (value, definition) => new
                {
                    value.DocumentId,
                    definition.Name,
                    definition.CreatedAt,
                    value.Value,
                    value.Ordinal,
                })
            .ToListAsync();

        var fieldsByDocument = fieldRows
            .GroupBy(r => r.DocumentId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ImapIndexField>)[.. g
                    .OrderBy(r => r.CreatedAt).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Ordinal)
                    .Select(r => new ImapIndexField(r.Name, r.Value))]);

        var details = new Dictionary<Guid, ImapMessageDetails>();
        foreach (var document in documents)
        {
            if (!currentVersions.TryGetValue(document.Id, out var version))
            {
                continue;
            }

            var createdBy = version.CreatedByUserId is { } uid && users.TryGetValue(uid, out var userName)
                ? userName
                : version.CreatedByServiceAccountId is { } said && serviceAccounts.TryGetValue(said, out var saName)
                    ? saName
                    : null;

            details[document.Id] = new ImapMessageDetails(
                Filed: version.CreatedAt,
                DocumentDate: version.DocumentDate,
                CreatedBy: createdBy,
                VersionNumber: version.VersionNumber,
                VersionCount: versionCounts.TryGetValue(document.Id, out var count) ? count : 0,
                SizeBytes: version.SizeBytes,
                SensitivityLabel: document.SensitivityLabelId is { } lid && labelNames.TryGetValue(lid, out var label) ? label : null,
                OcrLanguages: version.OcrLanguages,
                MaskName: document.MaskVersionId is { } mvid && maskNames.TryGetValue(mvid, out var mask) ? mask : null,
                IndexFields: fieldsByDocument.TryGetValue(document.Id, out var fields) ? fields : []);
        }

        return details;
    }
}
