using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Controllers;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Replaces a document's whole index-data set: validate, enforce the mail-routing claims, refuse a write to a
/// classifier-owned field, then delete and re-insert the <see cref="FieldValue"/> rows in the caller's order.
/// </summary>
/// <remarks>
/// Extracted from <c>DocumentMetadataController.SetIndexData</c> unchanged, because ADR 0794's combined
/// <c>PUT .../detail</c> must DELEGATE to the same logic rather than carry a second copy of it. That is not a
/// hypothetical risk here: <c>DocumentFinalizer.ApplyStagedClassificationAsync</c> already writes index data
/// its own way for the intray path, and a third copy is exactly how the fourth one comes to get a fix the
/// first three do not.
///
/// It APPLIES ONLY — it neither gates nor saves. The caller decides who may write (the rights and the
/// frozen/checked-out checks), owns the transaction, and fires the side effects after the commit, because the
/// combined endpoint's whole purpose is to do each of those ONCE for a save that touches several aspects.
/// </remarks>
public class IndexDataWriter(SimplArchiveDbContext dbContext, MailboxAddressClaims mailboxAddressClaims)
{
    /// <summary>
    /// Stages the replacement on the change tracker. Throws the same refusals the endpoint always threw —
    /// unknown field, too many values, a duplicate address claim, a classifier-owned field.
    /// </summary>
    public async Task ApplyAsync(
        Document document,
        IReadOnlyList<DocumentMetadataController.SetFieldValueGroup> fields,
        bool confirmDuplicateClaims,
        CancellationToken cancellationToken)
    {
        var documentId = document.Id;

        var fieldDefinitionIds = fields.Select(f => f.FieldDefinitionId).ToList();
        var fieldDefinitions = await dbContext.FieldDefinitions
            .Where(f => fieldDefinitionIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, cancellationToken);

        foreach (var field in fields)
        {
            if (!fieldDefinitions.TryGetValue(field.FieldDefinitionId, out var definition))
            {
                throw new FieldDefinitionNotFoundException($"Field definition '{field.FieldDefinitionId}' does not exist.");
            }

            // Multiplicity comes from EITHER the flag or the type (#703): `IsList` says so for any basic type,
            // and MultiSelect is a list by virtue of being one — grandfathered, so an existing MultiSelect
            // field keeps accepting many values without anybody having to set the flag on it.
            if (!definition.IsList && definition.DataType != FieldDataType.MultiSelect && field.Values.Count > 1)
            {
                throw new MultipleValuesNotAllowedException($"Field '{definition.Name}' does not allow multiple values.");
            }
        }

        // The mail-routing rules (#703): who may write a Mailbox's address list, and which claims it may
        // carry. Before the rewrite below, because it compares the request against the STORED list.
        await mailboxAddressClaims.EnforceAsync(
            documentId, document.Name, fieldDefinitions, fields, confirmDuplicateClaims, cancellationToken);

        var existingValues = await dbContext.FieldValues.Where(v => v.DocumentId == documentId).ToListAsync(cancellationToken);

        // The classifier-owned projection fields (ADRs 0743/0744) are read-only HERE, at the one entrance index
        // data is written through — the clients also hide the edit, but a rule enforced only there is not a
        // rule. This is a full replacement, so both directions are guarded: a CHANGED owned value and an
        // OMITTED one (which the replacement would silently erase) are refused alike; echoing the current
        // values back — what an honest client does with a read-only row — passes untouched.
        var documentMask = await dbContext.Documents
            .Where(d => d.Id == documentId)
            .Join(dbContext.MaskVersions, d => d.MaskVersionId, v => (Guid?)v.Id, (d, v) => new { v.MaskId, MaskVersionId = v.Id })
            .FirstOrDefaultAsync(cancellationToken);
        if (documentMask is not null && WellKnownMaskIds.ClassifierOwnedFields.TryGetValue(documentMask.MaskId, out var ownedFields))
        {
            // Scoped to the document's OWN mask version — "Start" on some other mask is somebody else's field.
            var ownedDefinitionIds = await dbContext.FieldDefinitions
                .Where(f => f.MaskVersionId == documentMask.MaskVersionId && ownedFields.Contains(f.Name))
                .Select(f => new { f.Id, f.Name })
                .ToListAsync(cancellationToken);
            foreach (var owned in ownedDefinitionIds)
            {
                var stored = existingValues
                    .Where(v => v.FieldDefinitionId == owned.Id)
                    .OrderBy(v => v.Ordinal).ThenBy(v => v.Id)
                    .Select(v => v.Value)
                    .ToList();
                var submitted = fields.FirstOrDefault(f => f.FieldDefinitionId == owned.Id)?.Values ?? [];
                if (!stored.SequenceEqual(submitted))
                {
                    throw new ClassifierOwnedFieldException(owned.Name);
                }
            }
        }

        dbContext.FieldValues.RemoveRange(existingValues);

        foreach (var field in fields)
        {
            // Stamped in the order the caller sent them (#703) — a list is what the user typed, so its order
            // is theirs. Without it the read came back in whatever order the database chose, and that order
            // changed between reads.
            for (var ordinal = 0; ordinal < field.Values.Count; ordinal++)
            {
                dbContext.FieldValues.Add(new FieldValue
                {
                    Id = Guid.NewGuid(),
                    TenantId = document.TenantId,
                    DocumentId = documentId,
                    FieldDefinitionId = field.FieldDefinitionId,
                    Value = field.Values[ordinal],
                    Ordinal = ordinal,
                });
            }
        }

        // Editing index data IS an edit to the document, and saying so is what makes it guardable (#1083).
        // This writes FieldValue CHILD rows; EF checks a concurrency token only on rows it is actually
        // updating, so while the Document row stayed untouched the parent's token never fired and the most
        // collision-prone edit in the app — two people on one document's fields — was unguarded. Marking the
        // token modified brings the row into the UPDATE, so the caller's If-Match is compared and a fresh
        // token is issued. It also means ONE version covers name, mask, sensitivity and fields alike, which is
        // how the pencil commits them anyway (ADR 0278).
        //
        // Here rather than at the caller because it is part of WRITING index data, not part of the envelope:
        // a caller that forgot it would silently reopen exactly the hole #1083 closed.
        dbContext.Entry(document).Property(d => d.ConcurrencyToken).IsModified = true;
    }
}
