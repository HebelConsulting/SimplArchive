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
    // The date's optional UTC time (ADR 0758). Carried because the pane shows the PAIR — a METAR's row reads
    // "2026-09-20 05:20 UTC" there — and IMAP showing only the date is the same surface disagreeing about the
    // same fact (ADR 0809). It is also what the envelope's Date must carry: a mail client sorts by it, and
    // five METARs filed on one day all landing at midnight sort by nothing at all.
    TimeOnly? DocumentTime,
    string? CreatedBy,
    int? VersionNumber,
    int VersionCount,
    long? SizeBytes,
    string? SensitivityLabel,
    string? OcrLanguages,
    // The three rows IMAP used to omit while the detail pane showed them (#1301). Their absence was the bulk
    // of the disagreement between the two surfaces: a document under a workflow, or under retention, said so
    // in the workbench and said nothing at all in a mail client.
    string? WorkflowStatus,
    string? OcrStatus,
    string? Retention,
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
        var maskVersionIds = documents.Where(d => d.MaskVersionId != Guid.Empty)
            .Select(d => d.MaskVersionId).Distinct().ToList();
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

        // The workflow status, batched like everything else here. A document with no WorkflowState has never
        // entered the workflow, which the pane renders as no row rather than as "none".
        // Keyed on the VERSION, which is where a workflow state lives — and which is also what the detail pane
        // reads (`current.WorkflowStatus`). Keying on the document would have compiled against nothing and,
        // worse, invited a per-document answer for a per-version fact.
        var currentVersionIds = currentVersions.Values.Select(v => v.Id).ToList();
        var workflowStates = await db.WorkflowStates.IgnoreQueryFilters()
            .Where(w => currentVersionIds.Contains(w.DocumentVersionId))
            .ToDictionaryAsync(w => w.DocumentVersionId, w => w.Status);

        // Retention comes from the MASK, not the document: the years live on the mask version, and the
        // disposition date is derived from the document's anchor. A legal hold suspends it, and saying so is
        // the whole point of the row — a document that looks disposable and is not.
        var retentionYears = await db.MaskVersions.IgnoreQueryFilters()
            .Where(m => maskVersionIds.Contains(m.Id) && m.RetentionYears != null)
            .ToDictionaryAsync(m => m.Id, m => m.RetentionYears!.Value);
        // A hold reaches a document through LegalHoldItem, and only an UNRELEASED hold freezes it. Batched
        // rather than asking ILegalHoldService per document, which would be one round trip per row on the path
        // a mail client hits every time it opens a folder — the reason this loader exists at all.
        var heldDocumentIds = retentionYears.Count == 0
            ? []
            : await db.LegalHoldItems.IgnoreQueryFilters()
                .Where(i => documentIds.Contains(i.DocumentId)
                    && db.LegalHolds.IgnoreQueryFilters().Any(h => h.Id == i.LegalHoldId && h.ReleasedAt == null))
                .Select(i => i.DocumentId)
                .Distinct()
                .ToListAsync();

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
                DocumentTime: version.DocumentTime,
                CreatedBy: createdBy,
                VersionNumber: version.VersionNumber,
                VersionCount: versionCounts.TryGetValue(document.Id, out var count) ? count : 0,
                SizeBytes: version.SizeBytes,
                SensitivityLabel: document.SensitivityLabelId is { } lid && labelNames.TryGetValue(lid, out var label) ? label : null,
                OcrLanguages: version.OcrLanguages,
                WorkflowStatus: workflowStates.TryGetValue(version.Id, out var status) ? status.ToString() : null,
                OcrStatus: version.OcrVerdict?.ToString(),
                Retention: RetentionRow(document, version, retentionYears, heldDocumentIds),
                MaskName: document.MaskVersionId is { } mvid && maskNames.TryGetValue(mvid, out var mask) ? mask : null,
                IndexFields: fieldsByDocument.TryGetValue(document.Id, out var fields) ? fields : []);
        }

        return details;
    }

    /// <summary>
    /// The retention row exactly as the pane words it: the years, the derived disposition date, and whether a
    /// legal hold has suspended it. Null when the document's mask sets no retention, which is the pane's
    /// "no row" rather than a row saying none.
    /// </summary>
    private static string? RetentionRow(
        Document document,
        DocumentVersion version,
        IReadOnlyDictionary<Guid, int> retentionYears,
        IReadOnlyList<Guid> heldDocumentIds)
    {
        if (document.MaskVersionId == Guid.Empty || !retentionYears.TryGetValue(document.MaskVersionId, out var years))
        {
            return null;
        }

        // The SAME anchor rule the document resource uses (DocumentsController.BuildRetentionInfoAsync): the
        // current version's document date, falling back to when the document was filed. A second rule here
        // would make the pane and the message disagree about a disposition date, which is the one number on
        // this row anybody acts on.
        var anchor = version.DocumentDate;
        var disposition = Documents.RetentionSchedule.DispositionDateOf(anchor, years);
        var suspended = heldDocumentIds.Contains(document.Id) ? " (suspended — legal hold)" : string.Empty;
        return $"{years} years · disposition {disposition:yyyy-MM-dd}{suspended}";
    }
}
