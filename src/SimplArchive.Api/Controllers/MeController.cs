using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Hypermedia;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The caller's own account — the one place a client finds everything that belongs to <em>them</em> rather than
/// to the tenant.
/// </summary>
/// <remarks>
/// Added for issue #416. The routes it links to all existed already; what did not exist was a resource to reach
/// them FROM, so every client composed <c>api/users/me/password</c>, <c>api/me/webdav-password</c> and the rest by
/// hand — 23 composed URLs across the two clients, the largest single cluster in the burn-down.
///
/// They do not belong on the <c>users</c> collection, which is the administrator's list of everyone, and they do
/// not belong individually on the API root: they are properties of one resource, and hanging nine of them off the
/// root would turn it into the flat URL registry that ADR 0543 argues against. A "me" resource is what the
/// hypermedia actually wanted.
///
/// Deliberately NOT a second <c>whoami</c>. <c>/api/diagnostics/whoami</c> answers "what does the server think of
/// this token" — principal type, tenant, claims, client-gating flags — and is the standing proof-point for the
/// auth chain. This answers "where do I go to change my own things". Overlapping identity fields are a small
/// price for two clear questions.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/me")]
[Authorize]
public class MeController : ControllerBase
{
    private readonly ICurrentUserAccessor _currentUser;
    private readonly SimplArchiveDbContext _dbContext;

    public MeController(ICurrentUserAccessor currentUser, SimplArchiveDbContext dbContext)
    {
        _currentUser = currentUser;
        _dbContext = dbContext;
    }

    public class MeResource : HypermediaResource
    {
        public Guid? UserId { get; set; }

        /// <summary>
        /// The caller's own email address — <c>null</c> for a principal that has no personal account.
        /// </summary>
        /// <remarks>
        /// Here rather than on <c>whoami</c> for the reason in the class remarks: this resource answers "my own
        /// account", and a profile screen that shows which account you are signed in as needs it (#464). The
        /// overlap with whoami's identity fields is the price of two clear questions, not an accident.
        /// </remarks>
        public string? Email { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        // A service account or platform administrator has no personal account, so none of these apply. The
        // resource still resolves — with no rels — rather than 404ing, so a client can ask without special-casing
        // its principal type, and a MISSING rel means "not available to you" exactly as everywhere else.
        if (_currentUser.UserId is not { } userId)
        {
            return Ok(new MeResource { Links = [new Link("self", "/api/me", "GET")] });
        }

        // One projection rather than loading the user: this action is otherwise pure links, and a profile
        // screen asking "who am I" should not cost a full entity load.
        var email = await _dbContext.Users
            .Where(u => u.Id == userId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken);

        return Ok(new MeResource
        {
            UserId = userId,
            Email = email,
            Links =
            [
                new Link("self", "/api/me", "GET"),
                new Link("changePassword", "/api/users/me/password", "PUT"),
                new Link("photo", "/api/users/me/photo", "PUT"),
                new Link("mfa", "/api/users/me/mfa", "GET"),
                new Link("mfaEnroll", "/api/users/me/mfa/enroll", "POST"),
                new Link("mfaEnable", "/api/users/me/mfa/enable", "POST"),
                new Link("passkeys", "/api/users/me/passkeys", "GET"),
                new Link("passkeyRegisterOptions", "/api/users/me/passkeys/register/options", "POST"),
                new Link("passkeyRegister", "/api/users/me/passkeys/register", "POST"),
                new Link("webdavPassword", "/api/me/webdav-password", "GET"),
                new Link("personalRepository", "/api/me/personal-repository", "GET"),
                new Link("notificationPreferences", "/api/notifications/preferences", "GET"),
            ],
        });
    }

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead]
    public IActionResult Head() => NoContent();
}
