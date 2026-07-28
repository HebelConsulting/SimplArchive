using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Documents;

// A node in a tenant's unified document tree — see ADR "Document/DocumentVersion data shape
// (entities-only slice)". A folder is simply a Document with zero DocumentVersion rows; a leaf document
// has one or more versions and (usually) a mask assignment. Metadata (FieldValue) and mask assignment
// live here, at the document level, not per-version — see the same ADR for why. A "repository" is now
// just a Document with ParentId == null — see ADR "Repository/Document unification". There's no separate
// RepositoryId: any query that needs "everything under this root" walks ParentId iteratively rather than
// using a denormalized column (same precedent as cascade delete/restore, ADR "Document delete/restore
// (recycle bin) implementation").
public class Document : ITenantScoped, IConcurrencyTracked, ISoftDeletable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? ParentId { get; set; }

    public required string Name { get; set; }

    public Guid? MaskVersionId { get; set; }

    // Data-classification / sensitivity label (ADR "Configurable sensitivity labels + upload defaults",
    // superseding the fixed enum of ADR 0399) — a per-tenant SensitivityLabelDefinition, or null = None.
    // Informational + searchable + watermark trigger; no access enforcement.
    public Guid? SensitivityLabelId { get; set; }

    // Exactly one of CreatedByUserId/CreatedByServiceAccountId is set — mirrors the identical fix already
    // applied to DocumentVersion.CreatedByUserId (ADR "Document version upload/download endpoints
    // (pragmatic slice)"). No interactive human-user login flow exists yet (ADR "ServiceAccount request
    // authentication foundation"), so the only real creator identity available today is a ServiceAccount
    // — see ADR "Repositories controller and Document creation".
    public Guid? CreatedByUserId { get; set; }

    public Guid? CreatedByServiceAccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // null = active; non-null = soft-deleted into the recycle bin, since this instant — see ADR "Document
    // recycle-bin data shape (schema-only slice)" and ADR "Document delete/restore (recycle bin)
    // implementation" for the cascade-delete/restore/query-filter behavior built on top of this column.
    // Manual purge (hard delete) is still deferred.
    public DateTimeOffset? DeletedAt { get; set; }

    // A per-document retention extension (ADR "Retention review-before-disposition"): when a records manager
    // Extends a document during disposition review, this holds the new "retain until" date. If set and still in
    // the future, the document is not eligible for disposition (neither the auto-sweep nor a manual dispose),
    // overriding the mask's computed disposition date. Null = no override (use the mask's period).
    public DateOnly? RetentionOverrideUntil { get; set; }

    // false (default) = inherit the parent's effective ACL; true = ignore ancestors and use only this
    // document's own AclEntry rows (which may legitimately be zero, meaning nobody but a tenant admin can
    // see it) — see ADR "Document ACL inheritance data shape (schema-only slice)". Schema only for now:
    // the actual walk-up-the-tree resolution algorithm is separate, future work.
    public bool BreaksInheritance { get; set; }

    // Check-out (exclusive edit lock) — see ADR "Document check-out / check-in". null = not checked out; a
    // set value is the User holding the lock (user-only; a ServiceAccount doesn't check out — this is an
    // interactive edit flow). While held by someone other than the caller, every content/metadata mutation
    // is refused (full edit-lock). CheckedOutByUserId/CheckedOutAt are set/cleared together (a CHECK
    // constraint enforces the pairing).
    public Guid? CheckedOutByUserId { get; set; }

    public DateTimeOffset? CheckedOutAt { get; set; }

    // When the pre-expiry warning was sent to the holder (ADR "Check-out expiry UX"), so the sweep warns once
    // per check-out. Set by the sweep; cleared on release/acquire (independent of the CheckedOut* pairing).
    public DateTimeOffset? CheckoutReminderSentAt { get; set; }

    // Import provenance for idempotent re-import — see ADR "Idempotent re-import". Both null for a natively-
    // created document; set to the exporting instance's tenant id + the archive's original document id when this
    // row was created by an import. A partial unique index on (TenantId, OriginTenantId, OriginDocumentId) makes
    // re-importing the same archive match this row (update or skip) instead of duplicating it.
    public Guid? OriginTenantId { get; set; }

    public Guid? OriginDocumentId { get; set; }

    // Marks a root Document (ParentId == null) as a user's personal repository (ADR "Per-user personal
    // repository"). Null on every ordinary/shared repository; a partial unique index on (TenantId,
    // PersonalOfUserId) enforces at most one personal repository per user.
    public Guid? PersonalOfUserId { get; set; }

    // Backs HTTP ETag/If-Match optimistic concurrency — see ADR "ETag / If-Match optimistic
    // concurrency". Never set manually; SimplArchiveDbContext.SaveChanges regenerates it automatically.
    public Guid ConcurrencyToken { get; set; }
}
