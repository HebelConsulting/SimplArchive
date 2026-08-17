// PORTED from the sister project SimplCalCon (Apache-2.0, same licence) — see ADR 0621. ADAPTED throughout:
// its collections are Calendar/AddressBook entities owned by a user and its ACL is a flags enum, whereas here a
// collection is a TYPED FOLDER anywhere in the archive tree and rights come from IEffectiveRightsCalculator —
// so the ownership check becomes the ACL walk, and DavTree stays the single place that knows the document model.
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.CalDav.Authentication;
using SimplArchive.Api.CalDav.Http;
using SimplArchive.Api.CalDav.Xml;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.CalDav;

[Authorize(AuthenticationSchemes = DavAuthenticationDefaults.Scheme)]
public abstract class DavControllerBase : ControllerBase
{
    protected Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    protected Guid CurrentTenantId =>
        Guid.TryParse(User.FindFirstValue(DavBasicAuthenticationHandler.TenantClaim), out var id) ? id : Guid.Empty;

    protected IActionResult ForbidDav() => Forbid(DavAuthenticationDefaults.Scheme);

    /// <summary>
    /// The Depth header (RFC 4918 §10.2), defaulting to the value a client means when it omits one. A PROPFIND
    /// with no Depth is `infinity` per the RFC, but every real client sends 0 or 1 and answering `infinity` on a
    /// large archive would be a denial of service against ourselves — so the default is 1 and deeper is clamped.
    /// </summary>
    protected int Depth(int fallback = 1) => Request.Headers["Depth"].ToString() switch
    {
        "0" => 0,
        "1" => 1,
        "infinity" => 1,
        _ => fallback,
    };

    /// <summary>The parsed PROPFIND/REPORT body, or null when absent/malformed (→ treated as allprop).</summary>
    protected Task<System.Xml.Linq.XElement?> ReadBodyAsync(CancellationToken cancellationToken) =>
        DavXml.ReadBodyAsync(Request, cancellationToken);

    /// <summary>
    /// The caller's rights on a document, straight from the ACL calculator — this replaces the sister project's
    /// owner check, because in an archive a typed folder is not owned by whoever syncs it.
    /// </summary>
    protected Task<EffectiveRights> RightsAsync(IEffectiveRightsCalculator rights, Guid documentId) =>
        rights.GetEffectiveRightsAsync(CurrentUserId, documentId);

    /// <summary>
    /// Scopes the request to the authenticated principal, exactly as the middleware did before the port: every
    /// read past this point is filtered by the tenant query filter, and rights resolve against this user.
    /// </summary>
    protected void ApplyPrincipal(IServiceProvider services)
    {
        ((CurrentTenantAccessor)services.GetRequiredService<ICurrentTenantAccessor>()).TenantId = CurrentTenantId;
        ((CurrentUserAccessor)services.GetRequiredService<ICurrentUserAccessor>()).UserId = CurrentUserId;
    }

    protected IActionResult MultiStatus(PropRequest request, IEnumerable<DavResource> resources) =>
        DavXml.MultiStatus(Xml.MultiStatus.Build(request, resources));
}
