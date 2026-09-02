using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Controllers;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Domain.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// The rels a caller may follow on a document, and the one capability that is deliberately not a rel.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <c>DocumentsController.Get</c>, which had grown to a single 316-line action. CLAUDE.md's
/// thin-controller principle (ADR 0571's recipe) puts the HTTP edge in the controller — bind, authorize,
/// delegate, shape — and this is none of those: it is 210 lines of conditional emission whose every condition
/// mirrors what the linked endpoint itself enforces, so that a rel's absence means "not available to you,
/// here, now" (ADR 0543) rather than an affordance the server will refuse.
/// </para>
/// <para>
/// It returns <c>CanCreateChildren</c> alongside the links because that value is computed here — from the
/// mask, the personal-root test and the caller's rights — and consumed by the resource. It is a FLAG rather
/// than a rel because its address is the children collection, which <c>children</c> already names: one URL
/// under two names is what ADR 0719 rules out (#854).
/// </para>
/// <para>
/// The five <c>Url.Action</c> calls that reach this controller's own actions name it explicitly here. Inside
/// the controller they relied on the ambient <c>ActionContext</c>; the ambient controller is still Documents
/// when this runs, so the generated URLs are identical — but naming it is what makes that true by construction
/// rather than by where the code happens to sit.
/// </para>
/// </remarks>
public sealed class DocumentResourceLinks
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly DocumentAccessService _access;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;

    public DocumentResourceLinks(
        SimplArchiveDbContext dbContext,
        DocumentAccessService access,
        ICurrentUserAccessor currentUserAccessor,
        IUserSystemRightsResolver userSystemRights)
    {
        _dbContext = dbContext;
        _access = access;
        _currentUserAccessor = currentUserAccessor;
        _userSystemRights = userSystemRights;
    }

    public async Task<(List<Link> Links, bool CanCreateChildren)> BuildAsync(
        IUrlHelper url,
        Guid documentId,
        Guid? parentId,
        EffectiveRights rights,
        DocumentsController.CheckoutInfo? checkedOut,
        bool isFolder,
        bool isArchive,
        bool externalLinksAllowed,
        CancellationToken cancellationToken)
    {
        var links = new List<Link>
        {
            new("self", url.Action(nameof(DocumentsController.Get), "Documents", new { documentId })!, "GET"),
            new("children", url.Action(nameof(DocumentChildrenController.ListChildren), "DocumentChildren", new { documentId })!, "GET"),
            new("ancestors", url.Action(nameof(DocumentsController.ListAncestors), "Documents", new { documentId })!, "GET"),
            new("mask", url.Action(nameof(DocumentMetadataController.GetMask), "DocumentMetadata", new { documentId })!, "GET"),
            new("index-data", url.Action(nameof(DocumentMetadataController.GetIndexData), "DocumentMetadata", new { documentId })!, "GET"),
            new("versions", $"/api/documents/{documentId}/versions", "GET"),
            // The document's collaboration thread (issue #382). Its absence is why renaming the route from
            // /comments to /chat broke both clients at all: with the rel, a route move is invisible to them.
            new("chat", $"/api/documents/{documentId}/chat", "GET"),
            new("references", $"/api/documents/{documentId}/references", "GET"),
            new("referencing-folders", url.Action(nameof(DocumentChildrenController.ListReferencingFolders), "DocumentChildren", new { documentId })!, "GET"),
            // This subtree as a downloadable archive (ADR "Repository export"). STATIC, like a version's
            // `restore`: the gate is the CanExport SYSTEM right, which a client already
            // holds from /diagnostics/whoami and uses to draw the affordance — so making the rel conditional
            // would buy nothing the client doesn't know and would put two system-rights lookups on the
            // hottest read in the app. The conditional rels here are the ones a client CANNOT work out for
            // itself (is this a folder, a zip, a root, does the tenant allow external links).
            new("export", url.Action(nameof(DocumentTransferController.Export), "DocumentTransfer", new { documentId })!, "GET"),
            new("set-primary-location", url.Action(nameof(DocumentsController.SetPrimaryLocation), "Documents", new { documentId })!, "PUT"),
            // The caller's PERSONAL colour for this collection (#564 slice 2, ADR 0620). Unconditional for the
            // same reason as tags/reminders below: anyone who may see a folder may choose how they see it, and
            // it is meaningless on a non-collection — a client draws the affordance only for a typed folder,
            // which it can tell from the mask it already has.
            new("collection-color", $"/api/documents/{documentId}/collection-color", "PUT"),
            new("assignable-reviewers", url.Action(nameof(DocumentsController.AssignableReviewers), "Documents", new { documentId })!, "GET"),
            // The caller's own relationship to this document. UNCONDITIONAL: anyone who may see a document may
            // read its tags, its own reminders and its own subscription. The rights that differ govern WRITING,
            // and a write answers for itself — hiding the address would not be "not available to you", it would
            // just make the client compose one (ADR 0543, issue #416).
            new("tags", $"/api/documents/{documentId}/tags", "GET"),
            new("reminders", $"/api/documents/{documentId}/reminders", "GET"),
            new("subscription", $"/api/documents/{documentId}/subscription", "GET"),
        };

        if (externalLinksAllowed)
        {
            links.Add(new Link("external-links", $"/api/documents/{documentId}/external-links", "GET"));
        }

        // Break/restore ACL inheritance (issue #426). CONDITIONAL for the same reason as external-links above: a
        // repository ROOT has no parent to inherit from, so the server always refuses there — and an affordance
        // whose only outcome is a refusal is exactly what ADR 0543 rules out. Both clients used to draw the
        // toggle on a root and hand the user the resulting 400.
        //
        // Gated on the caller's own CanManagePermissions too, matching what the PUT itself enforces, so the rel
        // is absent rather than leading to a 403. Neither client can work this out for itself: the resource
        // deliberately exposes no ParentId, because "is this a root" is the API's question to answer, not a fact
        // for two clients to reason about separately and drift on.
        // The document's grants (issue #416). Gated on the same right the collection's own GET enforces, so the
        // rel's absence is the manage-access affordance's answer rather than a 403 the client has to interpret.
        if (rights.CanManagePermissions)
        {
            links.Add(new Link("acl-entries", $"/api/documents/{documentId}/acl-entries", "GET"));
        }

        // Where this document LIVES (#761) — the rel Notifications, Reminders and LegalHolds items already
        // hand out, now on the resource itself: the deep-link lander holds an id, resolves the document, and
        // must open its containing folder without composing a URL or being told ParentId (which this resource
        // deliberately does not expose — see the inheritance note above). Absent on a repository root, which
        // IS the truthful answer: there is no containing folder to open.
        if (parentId is { } parentDocumentId)
        {
            links.Add(new Link("parent", $"/api/documents/{parentDocumentId}", "GET"));
        }

        if (parentId is not null && rights.CanManagePermissions)
        {
            links.Add(new Link("acl-inheritance", $"/api/documents/{documentId}/acl-entries/inheritance", "PUT"));
        }

        // Re-filing this item. The rel is not new — it was emitted UNCONDITIONALLY, which is why both clients
        // offered "Move to…" on documents the server would refuse to move (#858). Its presence is now the
        // answer, which is what ADR 0543 asks of a rel.
        //
        // A rel and not a flag, unlike CanDelete and CanEditIndexData beside it: this endpoint has an address
        // of its own, so ADR 0719's rule points the other way here.
        //
        // The condition mirrors the endpoint, INCLUDING the part that is easy to miss: a root has no parent, so
        // moving one demotes a repository and needs CanManageRepositories on top of CanMove. Gating on
        // `ParentId is not null` alone would have hidden a legitimate action from the people entitled to it.
        //
        // What its presence promises is `CanMove` on THIS item, which is only half of what the endpoint
        // enforces: a move also needs CanCreateSubItems on the TARGET, and no rel can answer that before a
        // target is chosen — the picker owns that half (ADR 0689). So the rel means "this item may be moved",
        // never "this move will succeed".
        if (rights.CanMove
            && (parentId is not null || await _access.HasManageRepositoriesRightAsync(cancellationToken)))
        {
            links.Add(new Link("move", url.Action(nameof(DocumentsController.Move), "Documents", new { documentId })!, "PUT"));
        }

        // Editable metadata, advertised only where the edit would actually be accepted (ADR 0554). Each gate
        // mirrors what its endpoint enforces — CanEditIndexData — so the rel's presence and the write's outcome
        // cannot disagree. The folder/document split is applicability, not permission: a folder has no
        // sensitivity label or OCR language, and a document has no contents order, so advertising either on the
        // wrong kind would offer an affordance that can only fail.
        // Only a ZIP has entries to list, so the rel is the server's answer to "can I browse inside this?" —
        // a question both clients previously answered themselves by comparing ".zip" against an extension they
        // had to carry. Needs read access, which is what the linked GET itself requires.
        if (isArchive && rights.CanReadContent)
        {
            links.Add(new Link("archive-entries", $"/api/documents/{documentId}/archive-entries", "GET"));
        }

        // Graft an export archive in under this folder (ADR "Repository import"). Right-gated like `export`
        // above and static for the same reason; the isFolder test is APPLICABILITY, not permission — an import
        // needs somewhere to put a subtree, and a leaf document is not that.
        if (isFolder)
        {
            links.Add(new Link("import", url.Action(nameof(DocumentTransferController.Import), "DocumentTransfer", new { documentId })!, "POST"));
        }

        // What a notebook holds (#564). CONDITIONAL, and on the mask rather than on a right: these
        // sub-resources do not EXIST on an ordinary folder, so their absence is the clients' whole test for
        // whether to offer "New section" / "New note". Without them each client would read a mask name off a
        // row and decide for itself — the same rule implemented twice, differently, and drifting from the
        // containment invariant that actually enforces it.
        // DocumentRow is a projection and does not carry the mask — one small query rather than widening the
        // row for every read that does not need it.
        var folderMaskId = await _dbContext.Documents
            .Where(d => d.Id == documentId && d.MaskVersionId != null)
            .Join(_dbContext.MaskVersions, d => d.MaskVersionId, v => v.Id, (_, v) => (Guid?)v.MaskId)
            .SingleOrDefaultAsync(cancellationToken);

        if (WellKnownMaskIds.TypedFolderRules.FirstOrDefault(r => r.FolderMaskId == folderMaskId) is { } typedRule
            && typedRule.Admits.Any(a => a.MaskId == WellKnownMaskIds.Note))
        {
            links.Add(new Link("sections", $"/api/documents/{documentId}/sections", "POST"));
            links.Add(new Link("notes", $"/api/documents/{documentId}/notes", "POST"));
        }

        // The contact and appointment creates (#631), by the same rule. Rights-gated here as the other creates
        // on this resource are — the row-level copies stay mask-only, because a per-row rights resolution is a
        // query per row on the hottest path there is.
        if (rights.CanCreateSubItems)
        {
            if (ChildCreationPolicy.AdmitsTypedItem(folderMaskId, WellKnownMaskIds.Contact))
            {
                links.Add(new Link("contacts", $"/api/documents/{documentId}/contacts", "POST"));
            }

            if (ChildCreationPolicy.AdmitsTypedItem(folderMaskId, WellKnownMaskIds.Appointment))
            {
                links.Add(new Link("appointments", $"/api/documents/{documentId}/appointments", "POST"));
            }
        }

        // "New subfolder", by the same rule as the two above — and it was the one affordance NOT gated this way
        // (#634). Both clients showed it unconditionally, so it appeared on a notebook, which admits sections
        // and notes but not folders; on a personal space's first level, which holds only what it was
        // provisioned with; on an ephemeral staging folder, which holds messages; and to a caller with no right
        // to create anything. In each case the user got a refusal instead of an absence, which is exactly what
        // ADR 0543 says a rel is for: its absence means "not available to you, here, now".
        //
        // The href is the children collection, which is also its GET rel — the METHOD is what differs, and a
        // separate rel name is what keeps a client's lookup-by-rel unambiguous.
        var isPersonalRoot = await _dbContext.Documents
            .AnyAsync(d => d.Id == documentId && d.PersonalOfUserId != null, cancellationToken);

        // Was the `create-child` rel, pointing at the SAME address as `children` and differing only by method
        // — one URL under two names (#854, ADR 0719). It is a capability, so it now says so.
        var canCreateChildren = rights.CanCreateSubItems && ChildCreationPolicy.AdmitsPlainChild(folderMaskId, isPersonalRoot);

        // The structured editors (#564, ADR 0631). Conditional on the MASK for the same reason as the notebook
        // rels above: a contact card exists on a contact and nowhere else, so the rel's absence is the clients'
        // whole test for whether to offer Edit — rather than each of them sniffing a file extension and
        // deciding for itself. Read-gated to match what the linked GET requires; a caller who may read but not
        // write still gets the rel, and the resource's own CanEdit tells the form which it is.
        //
        // Without these the endpoints are unreachable by a conforming client: the desktop composes no API URLs
        // at all, so an endpoint no rel reaches does not exist as far as it is concerned.
        if (rights.CanReadContent && folderMaskId == WellKnownMaskIds.Contact)
        {
            links.Add(new Link("contact-card", $"/api/documents/{documentId}/contact-card", "GET"));
        }

        if (rights.CanReadContent && folderMaskId == WellKnownMaskIds.Appointment)
        {
            links.Add(new Link("appointment", $"/api/documents/{documentId}/appointment", "GET"));
        }

        if (rights.CanEditIndexData)
        {
            if (isFolder)
            {
                links.Add(new Link("contents-sort-order", $"/api/documents/{documentId}/contents-sort-order", "PUT"));
            }
            else
            {
                links.Add(new Link("sensitivity", $"/api/documents/{documentId}/sensitivity", "PUT"));
                links.Add(new Link("ocr-languages", $"/api/documents/{documentId}/ocr-languages", "PUT"));
            }
        }

        // Check-out affordances (ADR "Document check-out / check-in"): offer check-out when it's free and the
        // caller can edit content; offer check-in when the caller holds the lock or can override someone else's.
        if (checkedOut is null && rights.CanEditContent)
        {
            links.Add(new Link("checkout", $"/api/documents/{documentId}/checkout", "PUT"));
        }
        else if (checkedOut is { ByMe: true }
                 || (checkedOut is not null && _currentUserAccessor.UserId is { } uid
                     && (await _userSystemRights.GetEffectiveSystemRightsAsync(uid, cancellationToken)).CanOverrideCheckout))
        {
            // Releasing the lock and DISCARDING the working copy — named for that, not "checkin", which on a
            // checkout row now means the POST that promotes the copy to a version (issue #416).
            links.Add(new Link("cancel-checkout", $"/api/documents/{documentId}/checkout", "DELETE"));
        }

        return (links, canCreateChildren);
    }
}
