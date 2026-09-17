using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Hypermedia;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Users;
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
    private readonly Concurrency.UserVerbs _users;

    public MeController(ICurrentUserAccessor currentUser, SimplArchiveDbContext dbContext, Concurrency.UserVerbs users)
    {
        _currentUser = currentUser;
        _dbContext = dbContext;
        _users = users;
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

        /// <summary>
        /// The zone this user's timestamps are rendered in, or <c>null</c> to follow their own device (#1254).
        /// </summary>
        /// <remarks>
        /// Null is the DEFAULT and the common case, not a missing value — the client resolves it from the
        /// browser or OS, which is the only place that answer exists. A non-null value is an explicit
        /// override, and "UTC" is simply one of them.
        /// </remarks>
        public string? DisplayTimeZoneId { get; set; }

        /// <summary>
        /// Whether crooked scans arriving in this user's intray are straightened automatically (#491).
        /// </summary>
        /// <remarks>
        /// A per-USER preference, because it is the person feeding the scanner who knows whether their scans
        /// come out crooked — and stored server-side rather than in a client's local settings, because the
        /// Worker's backstop sweep has to read it for items that arrive over WebDAV, where no client is
        /// involved at all.
        /// </remarks>
        public bool DeskewIntrayUploads { get; set; }

        /// <summary>
        /// Whether a batch scan arriving in this user's intray is cut into one item per document, at the Patch 3
        /// separator sheets between them (#492).
        /// </summary>
        /// <remarks>A sibling of the flag above in every respect — see its remarks for why it lives here.</remarks>
        public bool CutIntrayUploadsAtPatchCodes { get; set; }

        /// <summary>
        /// Whether a page arriving 90 or 180 degrees round is turned the right way up automatically (#492).
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="DeskewIntrayUploads"/> because the two cost differently: rotation on a PDF
        /// is only the page's <c>/Rotate</c> attribute and is therefore lossless, so it may run on PDFs as
        /// well as TIFFs — while deskew cannot happen without re-rendering the page. They shared one flag
        /// until the TIFF-only gate deskew needs was noticed to have been inherited by rotation, which needs
        /// no such thing.
        /// </remarks>
        public bool RotateIntrayUploads { get; set; }
    }

    /// <summary>The intended state of one on/off intray-ingest preference. Shared, because they are all this.</summary>
    public class PreferenceRequest
    {
        public bool Enabled { get; set; }
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
        var me = await _dbContext.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.Email, u.DeskewIntrayUploads, u.CutIntrayUploadsAtPatchCodes, u.RotateIntrayUploads, u.DisplayTimeZoneId })
            .FirstOrDefaultAsync(cancellationToken);

        return Ok(new MeResource
        {
            UserId = userId,
            Email = me?.Email,
            DeskewIntrayUploads = me?.DeskewIntrayUploads ?? true,
            RotateIntrayUploads = me?.RotateIntrayUploads ?? true,
            CutIntrayUploadsAtPatchCodes = me?.CutIntrayUploadsAtPatchCodes ?? true,
            // Null travels as null: it means "follow my device", which only the CLIENT can resolve, so the
            // server must not substitute its own zone here (#1254). Doing so would make every user inherit
            // whatever zone the container happens to run in.
            DisplayTimeZoneId = me?.DisplayTimeZoneId,
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
                new Link("imapAccess", "/api/me/imap-access", "GET"),
                new Link("personalRepository", "/api/me/personal-repository", "POST"),
                // The display-zone preference (#1254). Advertised so a client can FOLLOW it rather than
                // compose the address — an endpoint no resource links to is unreachable by a conforming
                // client, and therefore unfinished (ADR 0543).
                new Link("timeZone", "/api/me/time-zone", "PUT"),
            // The caller's addressbooks and calendars, for the Calendar/Contacts tabs (#564). The DAV home set
            // answers the same question for external clients; our own clients follow this rel and get JSON.
            new Link("davCollections", "/api/dav-collections", "GET"),
                new Link("notificationPreferences", "/api/notifications/preferences", "GET"),
                // The intray ribbon's toggles follow these rather than composing them (ADR 0543).
                new Link("deskewPreference", "/api/me/deskew", "PUT"),
                new Link("patchCodePreference", "/api/me/patch-codes", "PUT"),
                new Link("rotatePreference", "/api/me/rotate", "PUT"),
            ],
        });
    }

    /// <summary>Turns automatic straightening of intray scans on or off for the caller (#491).</summary>
    /// <remarks>
    /// A PUT of the intended value rather than a toggle: a toggle applied twice by a retry lands where it
    /// started, and the client already knows which state it wants.
    /// </remarks>
    [HttpPut("deskew")]
    public Task<IActionResult> SetDeskewPreference(
        [FromBody] PreferenceRequest request,
        CancellationToken cancellationToken) =>
        SetPreferenceAsync((user, enabled) => user.DeskewIntrayUploads = enabled, request.Enabled, cancellationToken);

    /// <summary>Turns automatic rotation of upside-down intray pages on or off for the caller (#492).</summary>
    [HttpPut("rotate")]
    public Task<IActionResult> SetRotatePreference(
        [FromBody] PreferenceRequest request,
        CancellationToken cancellationToken) =>
        SetPreferenceAsync((user, enabled) => user.RotateIntrayUploads = enabled, request.Enabled, cancellationToken);

    /// <summary>Turns automatic cutting of batch scans at their separator sheets on or off for the caller (#492).</summary>
    [HttpPut("patch-codes")]
    public Task<IActionResult> SetPatchCodePreference(
        [FromBody] PreferenceRequest request,
        CancellationToken cancellationToken) =>
        SetPreferenceAsync((user, enabled) => user.CutIntrayUploadsAtPatchCodes = enabled, request.Enabled, cancellationToken);

    /// <summary>The zone this user's timestamps are rendered in; null or empty follows their device (#1254).</summary>
    /// <remarks>
    /// <para>
    /// A PUT of the intended value, like its siblings above. Clearing it is a legitimate act and the way back
    /// to the default — so an empty body means "follow my device", not "ignore this request".
    /// </para>
    /// <para>
    /// VALIDATED AGAINST THE SHARED CHOICES rather than against this host's zone database. A server without
    /// tzdata would otherwise accept nothing at all, and this project has already shipped an image with none —
    /// every zoned calendar entry stored ~2 h out, silently, because no test host lacked it. Checking the list
    /// both clients pick from keeps the answer the same everywhere the app runs.
    /// </para>
    /// </remarks>
    [HttpPut("time-zone")]
    public Task<IActionResult> SetTimeZone(
        [FromBody] TimeZoneRequest request,
        CancellationToken cancellationToken)
    {
        var zoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? null : request.TimeZoneId.Trim();

        if (zoneId is not null && !SimplArchive.Presentation.TimeZoneChoices.Contains(zoneId))
        {
            throw new Errors.Exceptions.Principals.UnknownTimeZoneException(zoneId);
        }

        return SetPreferenceAsync((user, id) => user.DisplayTimeZoneId = id, zoneId, cancellationToken);
    }

    public class TimeZoneRequest
    {
        public string? TimeZoneId { get; set; }
    }

    // Which setting differs; nothing else does. Passed as a lambda at the call site so the difference and the
    // delegation read on one line, rather than as a second copy of the load-check-save. Generic over the VALUE
    // so a string preference reuses it rather than forking a near-identical copy (#1254).
    private async Task<IActionResult> SetPreferenceAsync<T>(
        Action<User, T> apply,
        T value,
        CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is not { } userId)
        {
            return Forbid();
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        // Through the user's verb contract (ADR 0795) — one call covering all three preferences, since every
        // one of them funnels here. They are the caller's OWN settings, so a collision is rarer than on an
        // admin screen, but the same session open in two tabs is enough.
        apply(user, value);
        await _users.MutateAsync(Request, user, apply: () => Task.CompletedTask, cancellationToken: cancellationToken);

        return NoContent();
    }

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead]
    public IActionResult Head() => NoContent();
}
