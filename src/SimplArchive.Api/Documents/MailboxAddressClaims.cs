using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Controllers;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Api.Errors.Exceptions.MailRouting;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// The rules around writing a Mailbox's address-claims list (#703): who may, and which claims it may carry.
/// </summary>
/// <remarks>
/// <para>
/// A named service rather than more controller body (the thin-controller principle): the index-data PUT calls
/// one method, and everything mail-routing-specific — the right, the uniqueness rule, the user-address rule,
/// and the three audit events — lives here where the next slice (LMTP delivery) can find it.
/// </para>
/// <para>
/// Enforced at the API boundary, not in <c>SaveChanges</c>, because every rule here is about the CALLER: which
/// right they hold, and whether they explicitly confirmed a duplicate. The DbContext cannot know either.
/// </para>
/// <para>
/// Addresses are compared case-insensitively throughout (the <c>NormalizedEmail</c> precedent, ADR 0150) but
/// stored as entered.
/// </para>
/// </remarks>
public sealed class MailboxAddressClaims(
    SimplArchiveDbContext dbContext,
    ICurrentServiceAccountAccessor currentServiceAccountAccessor,
    ICurrentUserAccessor currentUserAccessor,
    IUserSystemRightsResolver userSystemRights,
    IAuditRecorder audit)
{
    /// <summary>
    /// Enforces the claims rules for one index-data PUT, before any row is rewritten. A no-op unless the
    /// request CHANGES the stored list — including by omitting the field, which the full-replace PUT treats
    /// as deleting its rows.
    /// </summary>
    public async Task EnforceAsync(
        Guid documentId,
        string documentName,
        IReadOnlyDictionary<Guid, FieldDefinition> definitions,
        IReadOnlyList<DocumentMetadataController.SetFieldValueGroup> fields,
        bool confirmDuplicateClaims,
        CancellationToken cancellationToken)
    {
        // The field can be in play from EITHER side: the request writes it, or the document already carries
        // values on it. The second matters because the PUT replaces the whole field set — a request that
        // simply OMITS the field deletes its rows, and "silently release every claim" is exactly the kind of
        // change this gate exists for.
        var requestField = await AddressFieldOfAsync(definitions, cancellationToken);
        var stored = await dbContext.FieldValues
            .Where(v => v.DocumentId == documentId)
            .Join(dbContext.FieldDefinitions, v => v.FieldDefinitionId, f => f.Id, (v, f) => new { v, f })
            .Where(x => x.f.Name == WellKnownMaskSeeder.MailboxAddressesFieldName)
            .Join(dbContext.MaskVersions, x => x.f.MaskVersionId, mv => mv.Id, (x, mv) => new { x.v, mv.MaskId })
            .Where(x => x.MaskId == WellKnownMaskIds.Mailbox)
            .OrderBy(x => x.v.Ordinal).ThenBy(x => x.v.Id)
            .Select(x => new { x.v.FieldDefinitionId, x.v.Value })
            .ToListAsync(cancellationToken);

        if (requestField is null && stored.Count == 0)
        {
            return;
        }

        var fieldDefinitionId = requestField?.Id ?? stored[0].FieldDefinitionId;
        var proposed = requestField is null ? [] : fields.Single(f => f.FieldDefinitionId == requestField.Id).Values;
        var existing = stored.Select(x => x.Value).ToList();

        // An UNCHANGED list passes without the right: the PUT replaces the whole field set, so a caller
        // editing any other field on a mailbox resubmits this one too — gating on presence rather than on
        // change would lock every field on every mailbox to routing admins.
        if (proposed.SequenceEqual(existing, StringComparer.Ordinal))
        {
            return;
        }

        if (!await CallerMayRouteAsync(cancellationToken))
        {
            throw new MailRoutingRightRequiredException("Editing a mailbox's addresses");
        }

        var duplicateInList = proposed
            .GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateInList is not null)
        {
            throw new FieldValueInvalidException($"'{duplicateInList.Key}' is listed more than once.");
        }

        var added = proposed.Where(a => !existing.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
        var removed = existing.Where(a => !proposed.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();

        foreach (var address in added)
        {
            await EnforceClaimableAsync(documentId, documentName, fieldDefinitionId, address, confirmDuplicateClaims, cancellationToken);
        }

        foreach (var address in added)
        {
            await audit.RecordAsync(AuditActions.MailboxAddressClaimed, "Document", documentId, documentName,
                $"Claimed '{address}'", cancellationToken: cancellationToken);
        }

        foreach (var address in removed)
        {
            await audit.RecordAsync(AuditActions.MailboxAddressReleased, "Document", documentId, documentName,
                $"Released '{address}'", cancellationToken: cancellationToken);
        }
    }

    /// <summary>Refuses deleting or restoring a subtree containing a mailbox without the routing right (#703).</summary>
    /// <remarks>
    /// Both transitions change what the tenant's mail routing IS: delete is the moment addresses stop
    /// receiving (delivery never sees a recycled mailbox), restore the moment they start again. Purge stays
    /// ungated — routing already stopped at soft-delete. Owner-decided 2026-08-23. The whole SUBTREE is
    /// checked, not just the target: deleting a folder cascades to the mailbox three levels down, and gating
    /// only the direct target would make "delete the department" the one-step bypass of "delete its mailbox".
    /// </remarks>
    public async Task EnforceMayDeleteOrRestoreAsync(Guid documentId, string action, CancellationToken cancellationToken)
    {
        if (await SubtreeContainsMailboxAsync(documentId, cancellationToken)
            && !await CallerMayRouteAsync(cancellationToken))
        {
            throw new MailRoutingRightRequiredException(action);
        }
    }

    /// <summary>Whether any document in the subtree (soft-deleted included) wears the Mailbox mask.</summary>
    /// <remarks>Public beside the enforce wrapper because the recycle bin's BULK restore has skip-and-count
    /// semantics — it needs the answer, not the throw.</remarks>
    public async Task<bool> SubtreeContainsMailboxAsync(Guid rootId, CancellationToken cancellationToken)
    {
        // Level-wise walk, the CollectSubtreeAsync shape — but with the soft-delete filter off, because a
        // restore target and its descendants are recycled by definition.
        var currentLevel = new List<Guid> { rootId };
        while (currentLevel.Count > 0)
        {
            var level = currentLevel; // no closure over the loop variable
            var wearsMailbox = await dbContext.Documents
                .IgnoreQueryFilters(["SoftDeleteFilter"])
                .Where(d => level.Contains(d.Id) && d.MaskVersionId != null)
                .Join(dbContext.MaskVersions, d => d.MaskVersionId, mv => mv.Id, (d, mv) => mv.MaskId)
                .AnyAsync(maskId => maskId == WellKnownMaskIds.Mailbox, cancellationToken);
            if (wearsMailbox)
            {
                return true;
            }

            currentLevel = await dbContext.Documents
                .IgnoreQueryFilters(["SoftDeleteFilter"])
                .Where(d => d.ParentId != null && level.Contains(d.ParentId!.Value))
                .Select(d => d.Id)
                .ToListAsync(cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// The Mailbox mask's address field, if this request writes it. Identified by the seeder's field NAME on
    /// the Mailbox mask — a name is a well-known field's identity (the heal matches by it too).
    /// </summary>
    private async Task<FieldDefinition?> AddressFieldOfAsync(
        IReadOnlyDictionary<Guid, FieldDefinition> definitions, CancellationToken cancellationToken)
    {
        var candidates = definitions.Values
            .Where(d => d.Name == WellKnownMaskSeeder.MailboxAddressesFieldName)
            .ToList();

        foreach (var candidate in candidates)
        {
            var isMailboxMask = await dbContext.MaskVersions
                .AnyAsync(mv => mv.Id == candidate.MaskVersionId && mv.MaskId == WellKnownMaskIds.Mailbox, cancellationToken);
            if (isMailboxMask)
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task EnforceClaimableAsync(
        Guid documentId, string documentName, Guid fieldDefinitionId, string address,
        bool confirmDuplicateClaims, CancellationToken cancellationToken)
    {
        var normalized = address.ToUpperInvariant();

        // A user's personal address is theirs — hard reject, no override (concept default): claiming it would
        // silently divert a person's mail.
        if (await dbContext.Users.AnyAsync(u => u.NormalizedEmail == normalized, cancellationToken))
        {
            throw new UserAddressClaimNotAllowedException(address);
        }

        // One mailbox per address by default. The join to Documents applies the soft-delete filter, so a
        // recycled mailbox's old claims do not block re-claiming the address elsewhere.
        var claimedBy = await dbContext.FieldValues
            .Where(v => v.FieldDefinitionId == fieldDefinitionId
                && v.DocumentId != documentId
                && v.Value.ToUpper() == normalized)
            .Join(dbContext.Documents, v => v.DocumentId, d => d.Id, (v, d) => d.Name)
            .FirstOrDefaultAsync(cancellationToken);

        if (claimedBy is null)
        {
            return;
        }

        if (!confirmDuplicateClaims)
        {
            throw new DuplicateAddressClaimException(address, claimedBy);
        }

        // The override is a feature, not only a hazard: one address, a copy into each claiming mailbox. Its
        // own audit action because "an admin decided two mailboxes receive this address" is the fact an
        // auditor hunts for.
        await audit.RecordAsync(AuditActions.MailboxDuplicateClaimConfirmed, "Document", documentId, documentName,
            $"Confirmed duplicate claim of '{address}' (also claimed by '{claimedBy}')", cancellationToken: cancellationToken);
    }

    /// <summary>ServiceAccount column first, then the user's effective rights (own ∪ groups) — ADR 0209's order.</summary>
    public async Task<bool> CallerMayRouteAsync(CancellationToken cancellationToken)
    {
        if (currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await dbContext.ServiceAccounts
                .Where(s => s.Id == serviceAccountId)
                .Select(s => s.CanManageMailRouting)
                .SingleAsync(cancellationToken);
        }

        if (currentUserAccessor.UserId is { } userId)
        {
            return (await userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanManageMailRouting;
        }

        return false;
    }
}
