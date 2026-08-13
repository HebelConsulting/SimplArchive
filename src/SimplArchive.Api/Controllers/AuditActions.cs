namespace SimplArchive.Api.Controllers;

/// <summary>
/// Stable audit action codes (ADR "Audit trail (first slice)") — the <c>AuditEvent.Action</c> values. Kept
/// as constants so recording sites and any future filtering share one vocabulary.
/// </summary>
public static class AuditActions
{
    public const string AclGranted = "Acl.Granted";
    public const string AclRevoked = "Acl.Revoked";
    public const string AclInheritanceBroken = "Acl.InheritanceBroken";
    public const string AclInheritanceRestored = "Acl.InheritanceRestored";

    public const string DocumentDeleted = "Document.Deleted";
    public const string DocumentRestored = "Document.Restored";
    public const string DocumentMoved = "Document.Moved";

    // Document content/metadata lifecycle (ADR "Audit every-mutation coverage — document lifecycle").
    public const string DocumentCreated = "Document.Created";
    public const string RepositoryCreated = "Repository.Created";
    public const string DocumentRenamed = "Document.Renamed";
    public const string DocumentVersionAdded = "Document.VersionAdded";

    public const string DocumentVersionRestored = "Document.VersionRestored";
    public const string DocumentMaskAssigned = "Document.MaskAssigned";
    public const string DocumentMaskCleared = "Document.MaskCleared";
    public const string DocumentIndexDataUpdated = "Document.IndexDataUpdated";

    // External-system correlation key set/cleared on a document (ADR 0520).
    public const string DocumentOriginSet = "Document.OriginSet";
    public const string DocumentOriginCleared = "Document.OriginCleared";

    public const string DocumentTagsUpdated = "Document.TagsUpdated";
    public const string DocumentDateChanged = "Document.DocumentDateChanged";
    public const string DocumentContentsSortOrderChanged = "Document.ContentsSortOrderChanged";
    public const string DocumentOcrLanguagesChanged = "Document.OcrLanguagesChanged";
    // The data-classification / sensitivity label changed (ADR "Data classification / sensitivity labels").
    public const string DocumentSensitivityChanged = "Document.SensitivityChanged";

    // Collaboration events (ADR "Audit collaboration events" — the second half of every-mutation coverage).
    // Renamed from "Comment.Posted" with the rest of the model (issue #382). Changing a stored audit value is
    // normally the wrong move — the log is append-only and hash-chained, and its segments go to WORM storage, so
    // past events genuinely cannot be rewritten and one activity would end up with two irreconcilable names. It
    // is done here only because no deployment carries productive audit data yet, which is the single window in
    // which this is free. Once one does, this constant's value is frozen.
    public const string ChatMessagePosted = "Chat.MessagePosted";
    // External links (ADR 0546). Accessed is raised with an ExternalLink actor and NO principal; the others are
    // ordinary user actions. The token never appears in an event — the link id identifies the row instead.
    public const string ExternalLinkCreated = "ExternalLink.Created";
    public const string ExternalLinkAccessed = "ExternalLink.Accessed";
    public const string ExternalLinkExtended = "ExternalLink.Extended";
    public const string ExternalLinkRevoked = "ExternalLink.Revoked";

    // Raised when an existing link's URL is read back, which is possible only where the tenant opted in
    // (Tenant.ShowExternalLinkUrl, issue #412). Worth recording precisely because the token normally never
    // leaves the create response: once it can be retrieved, "who else obtained this URL" becomes a real
    // question, and it is the one asked after a link turns up somewhere it should not have.
    public const string ExternalLinkUrlViewed = "ExternalLink.UrlViewed";
    public const string ReferenceAdded = "Reference.Added";
    public const string ReferenceRemoved = "Reference.Removed";
    public const string AnnotationAdded = "Annotation.Added";
    public const string AnnotationEdited = "Annotation.Edited";
    public const string AnnotationRemoved = "Annotation.Removed";
    // Permanent removal of a recycle-bin document (ADR "Manual hard-delete / purge").
    public const string DocumentPurged = "Document.Purged";
    // Filed from the inbox — as a new document in a folder or as a new version of an existing one (ADR "Audit
    // tenant-settings, inbox filing + personal-repository creation").
    public const string DocumentFiled = "Document.Filed";
    // Import of an archive's root (ADR "Repository import").
    public const string DocumentImported = "Document.Imported";
    // Auto-disposed by the retention sweep (ADR "Retention policies (auto-disposition)"). Also used as a literal
    // in Infrastructure's RetentionService, which can't reference this Api-layer class.
    public const string DocumentRetentionDisposed = "Document.RetentionDisposed";

    // A records manager extended a document's retention during disposition review (ADR "Retention
    // review-before-disposition").
    public const string DocumentRetentionExtended = "Document.RetentionExtended";
    // Check-out (exclusive edit lock) — ADR "Document check-out / check-in".
    public const string DocumentCheckedOut = "Document.CheckedOut";

    // A save-by-rename edit through the WebDAV mount took the lock without anyone pressing "check out"
    // (ADR 0562). The detail carries the client's User-Agent — evidence of WHAT did it, never a condition.
    public const string DocumentCheckedOutImplicitly = "Document.CheckedOutImplicitly";
    public const string DocumentCheckedIn = "Document.CheckedIn";
    // The holder (or a CanOverrideCheckout admin) reset a check-out's idle timer (ADR "Self-service check-out
    // extension").
    public const string DocumentCheckoutExtended = "Document.CheckoutExtended";
    // A CanOverrideCheckout holder force-released someone else's lock.
    public const string DocumentCheckoutOverridden = "Document.CheckoutOverridden";
    // The background sweep auto-released a check-out idle past the tenant's TTL (ADR "Stale check-out
    // auto-release sweep"). Actor is System; mirrored as a literal in StaleCheckoutService.
    public const string DocumentCheckoutExpired = "Document.CheckoutExpired";

    public const string LegalHoldPlaced = "LegalHold.Placed";
    public const string LegalHoldReleased = "LegalHold.Released";
    public const string LegalHoldItemAdded = "LegalHold.ItemAdded";
    public const string LegalHoldItemRemoved = "LegalHold.ItemRemoved";

    public const string WorkflowSubmitted = "Workflow.Submitted";
    public const string WorkflowApproved = "Workflow.Approved";
    public const string WorkflowRejected = "Workflow.Rejected";
    public const string WorkflowReleased = "Workflow.Released";
    public const string WorkflowReassigned = "Workflow.Reassigned";

    public const string UserCreated = "User.Created";
    public const string UserDeactivated = "User.Deactivated";
    public const string UserReactivated = "User.Reactivated";
    public const string UserRightsChanged = "User.RightsChanged";
    public const string UserPasswordChanged = "User.PasswordChanged";
    public const string UserPasswordReset = "User.PasswordReset";
    public const string UserMfaEnabled = "User.MfaEnabled";
    public const string UserMfaDisabled = "User.MfaDisabled";
    public const string UserMfaReset = "User.MfaReset";
    public const string PasskeyRegistered = "User.PasskeyRegistered";
    public const string PasskeyRemoved = "User.PasskeyRemoved";

    public const string GroupCreated = "Group.Created";
    public const string GroupDeleted = "Group.Deleted";
    public const string GroupRightsChanged = "Group.RightsChanged";
    public const string GroupMemberAdded = "Group.MemberAdded";
    public const string GroupMemberRemoved = "Group.MemberRemoved";

    public const string ServiceAccountCreated = "ServiceAccount.Created";
    public const string ServiceAccountUpdated = "ServiceAccount.Updated";
    public const string ServiceAccountSecretRotated = "ServiceAccount.SecretRotated";
    public const string ServiceAccountRevoked = "ServiceAccount.Revoked";

    public const string TenantCreated = "Tenant.Created";
    // A tenant admin changed the tenant's self-service settings (ADR "Audit tenant-settings, inbox filing +
    // personal-repository creation") — the details carry the field-level before→after changes (secret redacted).
    public const string TenantSettingsUpdated = "Tenant.SettingsUpdated";

    // A tenant admin listed the tenant's users' personal spaces (ADR "Tenant-admin Administration → Users view")
    // — admin access to private spaces is recorded, not silent.
    public const string AdminViewedPersonalSpaces = "Admin.ViewedPersonalSpaces";

    public const string LoggedIn = "Auth.LoggedIn";
    // Impersonation token issued (ADR "User impersonation") — actor = the impersonating admin, target = the user.
    public const string ImpersonationStarted = "Auth.ImpersonationStarted";
}
