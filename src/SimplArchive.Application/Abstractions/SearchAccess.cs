namespace SimplArchive.Application.Abstractions;

// The caller's document-visibility context for a search query (ADR "Indexed ACL in search"). BypassAcl is
// true for a tenant admin (sees everything, no ACL filter applied). PrincipalTokens are the caller's ACL
// match tokens — their own id plus, for a User, every group they effectively belong to (direct memberships +
// those groups' descendants, since membership flows down the tree) — matched against a document's indexed
// allowedPrincipals. Empty tokens with no bypass ⇒ the caller sees nothing.
// MaxSensitivityRank is the caller's data-classification clearance ceiling (ADR "Sensitivity clearance
// enforcement") — the OpenSearch path additionally requires a hit's indexed sensitivityRank <= this value, so a
// document labelled above the caller's clearance is dropped. null = no ceiling (clearance not enforced, or the
// caller bypasses it — a tenant admin, which BypassAcl already covers). Only the OpenSearch path reads it; the
// metadata fallback is clearance-enforced instead by the controller's per-hit CanSee post-filter.
// PersonalSpacesRestrictedTo is the second half of the narrowed bypass (ADR 0670), and it exists because the
// bypass has TWO expressions: the calculator's, and this one. Narrowing only the calculator would have left
// admin SEARCH returning other users' personal documents while the tree and every listing had gone quiet — the
// most convincing possible way to ship a privacy feature that does not hold.
//
// Meaningful only when BypassAcl is true. Non-null ⇒ personal-space hits are dropped unless their indexed
// personalOf equals it (in practice: the caller's own space). Null ⇒ no personal restriction, which is the
// normal case, because a bypassing caller almost always also holds CanAccessWithoutGrant — promotion grants it.
// The restricted case is exactly the admin who revoked their own x-ray, and it must read as honestly absent.
public sealed record SearchAccess(
    bool BypassAcl,
    IReadOnlyCollection<string> PrincipalTokens,
    int? MaxSensitivityRank = null,
    Guid? PersonalSpacesRestrictedTo = null)
{
    public static readonly SearchAccess None = new(false, []);
}

// The prefixed token forms both indexed on a document (its CanSee grantees) and expanded for a caller — the
// prefixes keep the user/group/service-account id spaces distinct so they can share one keyword field.
public static class PrincipalToken
{
    public static string User(Guid id) => $"u:{id}";

    public static string Group(Guid id) => $"g:{id}";

    public static string ServiceAccount(Guid id) => $"s:{id}";
}
