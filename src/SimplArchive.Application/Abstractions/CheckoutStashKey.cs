namespace SimplArchive.Application.Abstractions;

// The per-user working-copy stash object key (ADR "Check-out working-copy stash + exit guard"):
// tenants/{tenantId}/users/{userId}/checkout/{documentId} (a sub-folder of the per-user private space, ADR
// "Per-user object-storage prefix") — a durable home for an in-progress working copy so the desktop can pause
// mid-edit and resume on next login, and the web can round-trip its edited file. Keyed by (userId, documentId)
// so a lock's stash is reclaimed on release and cannot orphan. A pure function shared by the Api
// (CheckoutsController / DocumentsController) and the Infrastructure stale-check-out sweep, so the key format
// lives in exactly one place.
public static class CheckoutStashKey
{
    public static string Build(Guid tenantId, Guid userId, Guid documentId) =>
        $"tenants/{tenantId}/users/{userId}/checkout/{documentId}";
}
