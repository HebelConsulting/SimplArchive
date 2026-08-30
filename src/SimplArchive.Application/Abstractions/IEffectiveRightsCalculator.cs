namespace SimplArchive.Application.Abstractions;

// Computes a principal's effective rights on a Document — see ADR "Effective rights computation", ADR
// "Document ACL inheritance resolution". A "repository-level" grant is now just a grant on a root Document
// (ParentId == null) — see ADR "Repository/Document unification", which collapsed the separate Repository-
// scope methods this interface used to have (GetEffectiveRightsAsync/GetEffectiveRightsForServiceAccountAsync
// took a repositoryId) into these two, since there's no more Repository/Document distinction to disambiguate.
// Implemented in SimplArchive.Infrastructure, where the actual query logic lives.
public interface IEffectiveRightsCalculator
{
    // Resolves the inherit-with-override walk (ADR "Permissions / access control model"): walks up from
    // documentId to the nearest ancestor with BreaksInheritance = true (or the document itself), falling
    // back to the nearest root ancestor's own grants if no override exists anywhere in the chain.
    Task<EffectiveRights> GetEffectiveRightsAsync(Guid userId, Guid documentId, CancellationToken cancellationToken = default);

    // A dedicated method, not an overload of the one above — a ServiceAccount can't belong to a Group
    // (GroupMembership.UserId is a real FK to User, not polymorphic) and has no IsTenantAdmin-equivalent
    // bypass, so its computation is a genuinely simpler, different code path — see ADR "ServiceAccount
    // effective rights computation", ADR "ServiceAccount Document-scope effective rights".
    Task<EffectiveRights> GetEffectiveRightsForServiceAccountAsync(Guid serviceAccountId, Guid documentId, CancellationToken cancellationToken = default);

    // The page-at-once forms of the two above (#858). A listing has to answer "may this caller delete/rename/
    // move THIS row?" per row to gate the destructive affordances honestly (ADR 0543), and the per-document
    // methods cost ~5 constant queries plus one per ancestor level — several hundred round trips for a 50-row
    // page, on the hottest read in the app. These collapse the per-principal work to once and resolve the
    // per-document parts set-based, so a page costs about the same as a single document at the same depth.
    //
    // Returns one entry per DISTINCT id; the single-document methods now delegate here rather than keeping a
    // second implementation of the same rules, so the two cannot drift.
    Task<IReadOnlyDictionary<Guid, EffectiveRights>> GetEffectiveRightsForManyAsync(
        Guid userId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, EffectiveRights>> GetEffectiveRightsForManyForServiceAccountAsync(
        Guid serviceAccountId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default);

    // Indexed-ACL support (ADR "Indexed ACL in search"). The prefixed principal tokens (u:/g:/s:) granted
    // CanSee on a document's *governing* ACL scope — indexed as the document's allowedPrincipals so search
    // can pre-filter by visibility instead of post-filtering each hit.
    Task<IReadOnlyCollection<string>> GetVisibilityPrincipalsAsync(Guid documentId, CancellationToken cancellationToken = default);

    // The caller's search-access context (match tokens + tenant-admin bypass) — the query-time side of
    // indexed-ACL. The User form expands to their groups' descendants (membership flows down); the
    // ServiceAccount form is just its own token (no groups, no admin bypass).
    Task<SearchAccess> GetSearchAccessForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<SearchAccess> GetSearchAccessForServiceAccountAsync(Guid serviceAccountId, CancellationToken cancellationToken = default);
}
