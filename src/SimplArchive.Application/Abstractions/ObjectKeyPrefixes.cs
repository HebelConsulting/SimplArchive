namespace SimplArchive.Application.Abstractions;

/// <summary>
/// The ONE place every object-storage key PREFIX is defined (the standing "all S3 key prefixes are
/// parametrized constants in one class-file" principle). The bucket layout — the fixed <c>tenants/…</c> path
/// structure under which everything lives — reads top-to-bottom here, and nowhere else in the codebase writes
/// a <c>"tenants/…"</c> literal (a unit test enforces it). Callers COMPOSE full keys by appending their own
/// variable leaf (a version guid, a filename) to a prefix from here; the leaf and any derived-artifact suffix
/// stay the caller's. Every prefix is tenant-rooted, which is also what lets the per-tenant bucket be derived
/// from any key (S3ObjectStorageClient.BucketFor) — a non-tenant-rooted key is a programming error.
/// </summary>
public static class ObjectKeyPrefixes
{
    /// <summary>The root every object key/prefix starts with — used by the bucket-deriving parser too.</summary>
    public const string Root = "tenants/";

    /// <summary>Everything a tenant owns: <c>tenants/{tenantId}/</c>.</summary>
    public static string Tenant(Guid tenantId) => $"{Root}{tenantId}/";

    /// <summary>All of a tenant's per-user areas: <c>tenants/{tenantId}/users/</c> (a listing root).</summary>
    public static string TenantUsers(Guid tenantId) => $"{Tenant(tenantId)}users/";

    /// <summary>A document version's content folder: <c>tenants/{tenantId}/{filingYear}/{storageFolderId}/</c>
    /// (ADR 0064/0217). The caller appends <c>{versionId}{ext}</c> and derived artifacts nest under it.</summary>
    public static string DocumentContentFolder(Guid tenantId, int filingYear, Guid storageFolderId) =>
        $"{Tenant(tenantId)}{filingYear}/{storageFolderId}/";

    /// <summary>A user's ephemeral (personal) mail store: <c>tenants/{tenantId}/users/{userId}/mail/</c>.</summary>
    public static string UserMail(Guid tenantId, Guid userId) => $"{User(tenantId, userId)}mail/";

    /// <summary>A department mailbox's mail store: <c>tenants/{tenantId}/mailboxes/{mailboxDocumentId}/mail/</c>.</summary>
    public static string MailboxMail(Guid tenantId, Guid mailboxDocumentId) =>
        $"{Tenant(tenantId)}mailboxes/{mailboxDocumentId}/mail/";

    /// <summary>A user's check-out working-copy stash: <c>tenants/{tenantId}/users/{userId}/checkout/</c>.</summary>
    public static string UserCheckout(Guid tenantId, Guid userId) => $"{User(tenantId, userId)}checkout/";

    /// <summary>A user's intray (inbox): <c>tenants/{tenantId}/users/{userId}/inbox/</c>.</summary>
    public static string UserInbox(Guid tenantId, Guid userId) => $"{User(tenantId, userId)}inbox/";

    /// <summary>A group's intray: <c>tenants/{tenantId}/groups/{groupId}/inbox/</c>.</summary>
    public static string GroupInbox(Guid tenantId, Guid groupId) => $"{Tenant(tenantId)}groups/{groupId}/inbox/";

    /// <summary>The bytes an intray overwrite set aside (its previous content): <c>…/users/{userId}/inbox-previous/</c>.</summary>
    public static string UserInboxPrevious(Guid tenantId, Guid userId) => $"{User(tenantId, userId)}inbox-previous/";

    /// <summary>A user's WebDAV scratch area: <c>tenants/{tenantId}/users/{userId}/temp/</c>.</summary>
    public static string UserTemp(Guid tenantId, Guid userId) => $"{User(tenantId, userId)}temp/";

    /// <summary>A user's WebDAV check-out scratch: <c>tenants/{tenantId}/users/{userId}/checkout-scratch/</c>.</summary>
    public static string UserCheckoutScratch(Guid tenantId, Guid userId) => $"{User(tenantId, userId)}checkout-scratch/";

    /// <summary>A user's WebDAV safe-save staging: <c>tenants/{tenantId}/users/{userId}/safe-save/</c>.</summary>
    public static string UserSafeSave(Guid tenantId, Guid userId) => $"{User(tenantId, userId)}safe-save/";

    /// <summary>A user's WebDAV safe-save shadow: <c>tenants/{tenantId}/users/{userId}/shadow/</c>.</summary>
    public static string UserShadow(Guid tenantId, Guid userId) => $"{User(tenantId, userId)}shadow/";

    /// <summary>A user's WebDAV stash-previous area: <c>tenants/{tenantId}/users/{userId}/stash-previous/</c>.</summary>
    public static string UserStashPrevious(Guid tenantId, Guid userId) => $"{User(tenantId, userId)}stash-previous/";

    /// <summary>A user's WebDAV set-aside area: <c>tenants/{tenantId}/users/{userId}/set-aside/</c>.</summary>
    public static string UserSetAside(Guid tenantId, Guid userId) => $"{User(tenantId, userId)}set-aside/";

    /// <summary>A tenant's WORM audit segments: <c>tenants/{tenantId}/audit-worm/</c> (ADR 0356).</summary>
    public static string AuditWorm(Guid tenantId) => $"{Tenant(tenantId)}{AuditWormSegment}/";

    /// <summary>The WORM segment name, exposed for callers that already qualify it themselves.</summary>
    public const string AuditWormSegment = "audit-worm";

    private static string User(Guid tenantId, Guid userId) => $"{Tenant(tenantId)}users/{userId}/";
}
