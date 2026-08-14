namespace SimplArchive.Infrastructure.Inbox;

/// <summary>
/// Where an inbox's objects live. One definition, because two callers need it and they cannot see each other:
/// the Api's scope resolver and the Worker's backstop sweep (issue #494).
/// </summary>
/// <remarks>
/// A prefix formula duplicated across a layer boundary is the kind of copy that survives a rename of the
/// storage layout in one place only — and the symptom would be a sweep that silently finds nothing, which
/// reads as "there was nothing to do".
/// </remarks>
public static class InboxScopePrefix
{
    public static string ForUser(Guid tenantId, Guid userId) => $"tenants/{tenantId}/users/{userId}/inbox/";

    // A group inbox is the exact peer of the per-user inbox, keyed by group (ADR 0532).
    public static string ForGroup(Guid tenantId, Guid groupId) => $"tenants/{tenantId}/groups/{groupId}/inbox/";
}
