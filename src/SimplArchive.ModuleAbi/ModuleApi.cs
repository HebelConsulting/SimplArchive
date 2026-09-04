namespace SimplArchive.ModuleAbi;

/// <summary>
/// The error a module controller throws (ADR 0737): a stable machine code, an HTTP status, a message. The
/// host's global handler translates it into the same RFC 7807 problem response a core refusal gets, so a
/// module's errors are indistinguishable on the wire from native ones. Modules derive intent-named
/// subclasses per condition — the core's specific-exception rule applies to module code unchanged.
/// </summary>
public class ModuleApiException : Exception
{
    /// <summary>Creates the error with its wire facts.</summary>
    public ModuleApiException(string errorCode, int statusCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    /// <summary>The stable machine-readable code (`FLIGHT_AIRCRAFT_GROUNDED`) a client branches on.</summary>
    public string ErrorCode { get; }

    /// <summary>The HTTP status the response carries.</summary>
    public int StatusCode { get; }
}

/// <summary>
/// Who is calling a module endpoint (ADR 0737) — the same answer the core's principal accessors give its
/// own controllers, resolved per request by the host. Exactly one of the two principal ids is set.
/// </summary>
public interface IModuleCallerContext
{
    /// <summary>The tenant the request runs in — every module read and write is scoped to it.</summary>
    Guid TenantId { get; }

    /// <summary>The interactive user, when one is calling.</summary>
    Guid? UserId { get; }

    /// <summary>The service account, when a machine principal is calling.</summary>
    Guid? ServiceAccountId { get; }

    /// <summary>Whether the caller holds the tenant-admin bypass — the same answer core gates read
    /// (a user's own flag ∪ their groups', resolved by the core; a service account never has it).</summary>
    Task<bool> IsTenantAdminAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller's human-readable identity (ABI 0.2, #1014): a user's display name and e-mail, a service
    /// account's name with no e-mail. What lets a handler prefill "who is acting" into a field a person
    /// will read — the GUIDs above are for machines. Null when no principal is resolved (which a module
    /// endpoint should never see; the activation gate refused the anonymous case already).
    /// </summary>
    Task<ModuleCallerIdentity?> GetIdentityAsync(CancellationToken cancellationToken = default);
}

/// <summary>The caller as a person would name them (ABI 0.2): display name, and e-mail when the principal
/// kind has one (a user does; a service account does not).</summary>
public sealed record ModuleCallerIdentity(string DisplayName, string? Email);

/// <summary>
/// A module controller's ask for a caller's effective rights on a document (ADR 0737) — the core
/// calculator's answer (inherit-with-override walk, group expansion, admin bypass), never a module's own
/// reimplementation of it.
/// </summary>
public interface IModuleDocumentRights
{
    /// <summary>The caller's effective rights on one document; all-false when it does not exist.</summary>
    Task<ModuleDocumentRightsAnswer> GetAsync(Guid documentId, CancellationToken cancellationToken = default);
}

/// <summary>The rights a module gate can ask about — the core right set, complete, by its own names.</summary>
public sealed record ModuleDocumentRightsAnswer(
    bool CanSee,
    bool CanReadContent,
    bool CanEditContent,
    bool CanEditIndexData,
    bool CanCreateSubItems,
    bool CanDelete,
    bool CanManagePermissions,
    bool CanMove,
    bool CanAnnotate);

/// <summary>
/// A rel a module contributes to the API ROOT for tenants where it is ACTIVE (ADR 0737) — the module's
/// entry into the hypermedia graph. Everything else the module serves links onward from here; paths stay
/// module-private (rel names, not routes, are the compatibility surface).
/// </summary>
/// <param name="Rel">The relation name a client navigates by — prefix it with the module id
/// (<c>test-module:status</c>) so two modules cannot collide.</param>
/// <param name="Path">The absolute path the rel reaches (<c>/api/test-module/status</c>).</param>
/// <param name="Method">The HTTP method.</param>
public sealed record ModuleRootLink(string Rel, string Path, string Method);
