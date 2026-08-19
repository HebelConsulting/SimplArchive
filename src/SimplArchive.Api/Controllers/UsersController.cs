using System.Security.Cryptography;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Principals;
using SimplArchive.Api.Errors.Exceptions.Authorization;
using SimplArchive.Api.Errors.Exceptions.Users;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Users;
using SimplArchive.Domain.Workflow;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Implements ADR "User/Group management endpoints" — the only Api path to create/rename/deactivate a
/// User; previously this only ever happened via direct DB seeding. Every action requires the caller's own
/// CanManageUsers — either a ServiceAccount or a logged-in User (see ADR "User support for
/// ServiceAccount/User/Group/Mask management endpoints"). IsTenantAdmin and every system-level right
/// always start false and aren't settable here — a ServiceAccount caller has no equivalent for most of
/// them to cap an escalation check against, so granting them remains separate, unimplemented work.
/// Password provisioning/self-service change — see ADR "Interactive User login (foundation slice)".
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly IAuditRecorder _audit;
    private readonly INotificationService _notifications;
    private readonly Authentication.MfaService _mfa;
    private readonly PasswordHasher<User> _passwordHasher = new();

    private readonly Documents.PersonalRepositoryProvisioner _personalSpaces;

    public UsersController(
        SimplArchiveDbContext dbContext,
        ICurrentTenantAccessor currentTenantAccessor,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        IUserSystemRightsResolver userSystemRights,
        IClearanceResolver clearanceResolver,
        IAuditRecorder audit,
        INotificationService notifications,
        Authentication.MfaService mfa,
        ITransitEncryptor transit,
        Documents.PersonalRepositoryProvisioner personalSpaces)
    {
        _personalSpaces = personalSpaces;
        _dbContext = dbContext;
        _currentTenantAccessor = currentTenantAccessor;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _userSystemRights = userSystemRights;
        _clearanceResolver = clearanceResolver;
        _audit = audit;
        _notifications = notifications;
        _mfa = mfa;
        _transit = transit;
    }

    private readonly ITransitEncryptor _transit;
    private readonly IClearanceResolver _clearanceResolver;

    // Plain mutable classes, not records — System.Xml.Serialization.XmlSerializer (ADR "JSON/XML content
    // negotiation") needs a parameterless constructor and settable properties.
    // The tenant-visible identity card (ADR 0544) — a deliberately small projection of a User. Plain mutable
    // class with a parameterless ctor, like every resource here (XmlSerializer, ADR "JSON/XML content negotiation").
    public class UserCardResource : HypermediaResource
    {
        public Guid UserId { get; set; }

        public string DisplayName { get; set; } = "";

        public string Email { get; set; } = "";

        // A deactivated colleague still authored past messages, so the card renders but can say so.
        public bool IsActive { get; set; }

        // Whether a photo exists; the bytes come from the "photo" rel, which is only present when this is true.
        public bool HasPhoto { get; set; }
    }

    public class UserResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Email { get; set; } = "";

        public string DisplayName { get; set; } = "";

        public bool IsActive { get; set; }

        // Whether this user has two-factor authentication enabled (ADR "MFA (interactive login, TOTP)") — the
        // admin tab shows the status + a Reset MFA action.
        public bool MfaEnabled { get; set; }

        // The tenant-wide system-level rights (ADR "Users & groups administration tab") — read here, set
        // via PUT /api/users/{id}/rights.
        public SystemRights Rights { get; set; } = new();
    }

    public class UsersListResource : HypermediaResource
    {
        public List<UserResource> Users { get; set; } = [];
    }

    public class CreateUserRequest
    {
        public string Email { get; set; } = "";

        public string DisplayName { get; set; } = "";

        // Optional admin-provisioned initial credential — this project has no email-sending capability
        // for an invite-link flow, so a CanManageUsers holder sets it directly. A User created without
        // one can't log in until PUT /users/me/password is used (which itself requires already being
        // logged in) or a future admin-reset endpoint sets one. See ADR "Interactive User login
        // (foundation slice)".
        public string? Password { get; set; }
    }

    public class RenameUserRequest
    {
        public string DisplayName { get; set; } = "";
    }

    public class ChangePasswordRequest
    {
        public string CurrentPassword { get; set; } = "";

        public string NewPassword { get; set; } = "";
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = _currentTenantAccessor.TenantId!.Value,
            Email = request.Email,
            DisplayName = request.DisplayName,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        if (!string.IsNullOrEmpty(request.Password))
        {
            user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);
        }

        _dbContext.Users.Add(user);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // (TenantId, NormalizedEmail) is a real DB unique index, no app-level pre-check — same shape
            // ServiceAccount.Name hit in ADR "ServiceAccount management endpoints".
            throw new UserEmailConflictException();
        }

        // The personal space is provisioned HERE, at creation, rather than on the user's first visit (#634).
        // Lazily was enough while it held only folders the user could recreate; it is not now that the first
        // level is closed and My Documents is the only place their own content may go — a user whose space does
        // not exist yet has nowhere to put anything, and every protocol surface resolves against those folders.
        //
        // The lazy EnsureAsync calls elsewhere stay: they are what reaches users created BEFORE this line
        // existed, which is the population a fresh-volume test never has (#574).
        await _personalSpaces.EnsureAsync(user.Id, user.TenantId, cancellationToken);

        await _audit.RecordAsync(AuditActions.UserCreated, "User", user.Id, user.DisplayName, cancellationToken: cancellationToken);

        var resource = BuildResource(user);

        return CreatedAtAction(nameof(Get), new { userId = user.Id }, resource);
    }

    // Cursor-based pagination (?cursor=&limit=) — see ADR "Pagination for list endpoints". Sorted
    // CreatedAt ascending, Id ascending as tiebreaker.
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);

        var query = _dbContext.Users.AsQueryable();

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(u => u.CreatedAt > cursorCreatedAt || (u.CreatedAt == cursorCreatedAt && u.Id > cursorId));
        }

        var fetched = await query.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id).Take(pageSize + 1).ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        var links = new List<Link> { new("self", Url.Action(nameof(List), new { cursor, limit = pageSize })!, "GET") };

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(List), new { cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        return Ok(new UsersListResource
        {
            Users = page.Select(BuildResource).ToList(),
            Links = links,
        });
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not relying
    // on ASP.NET Core to strip GET's body automatically.
    [HttpHead]
    public async Task<IActionResult> HeadList(CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
    }

    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> Get(Guid userId, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        var user = await _dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        return Ok(BuildResource(user));
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not relying
    // on ASP.NET Core to strip GET's body automatically.
    [HttpHead("{userId:guid}")]
    public async Task<IActionResult> Head(Guid userId, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        var exists = await _dbContext.Users.AnyAsync(u => u.Id == userId, cancellationToken);

        return exists ? NoContent() : NotFound();
    }

    // Renames DisplayName only — the same narrow-PUT contract as DocumentsController.Rename (ADR
    // "DocumentVersionsController resource-oriented redesign"). Email is immutable through this endpoint.
    [HttpPut("{userId:guid}")]
    public async Task<IActionResult> Rename(Guid userId, [FromBody] RenameUserRequest request, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        var user = await _dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        user.DisplayName = request.DisplayName;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(BuildResource(user));
    }

    // Deactivates: sets IsActive = false in place, row not deleted. Reversible (unlike ServiceAccount's
    // one-way revoke, ADR "ServiceAccount management endpoints") — a User is a person, not a credential;
    // see Reactivate below. See ADR "User/Group management endpoints".
    //
    // Workflow review reassignment (ADR "Workflow review reassignment"): a deactivated user gets no rights,
    // so any "In Review" task still assigned to them would be orphaned (no one could act on it). Deactivation
    // is therefore refused (409) when the user holds pending reviews unless ?reassignReviewsTo=<userId> hands
    // them to a replacement — an active tenant User other than the one being deactivated — in which case each
    // pending task is reassigned (a WorkflowState.InReview → InReview transition), the replacement notified,
    // and the reassignment audited, all before the user is deactivated. Tasks on soft-deleted documents don't
    // count (the join to the query-filtered Documents set excludes them — they aren't actionable anyway).
    [HttpDelete("{userId:guid}")]
    public async Task<IActionResult> Deactivate(Guid userId, [FromQuery] Guid? reassignReviewsTo, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        var user = await _dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        var pending = await (
            from w in _dbContext.WorkflowStates
            where w.AssignedToUserId == userId && w.Status == WorkflowStatus.InReview
            join v in _dbContext.DocumentVersions on w.DocumentVersionId equals v.Id
            join d in _dbContext.Documents on v.DocumentId equals d.Id // inner join → soft-deleted docs excluded
            select new { State = w, DocumentId = d.Id, DocumentName = d.Name })
            .ToListAsync(cancellationToken);

        User? replacement = null;
        if (pending.Count > 0)
        {
            if (reassignReviewsTo is not { } replacementId)
            {
                throw new ReviewerHasPendingReviewsException(pending.Count);
            }

            replacement = await _dbContext.Users.SingleOrDefaultAsync(u => u.Id == replacementId, cancellationToken);
            if (replacement is null || !replacement.IsActive || replacement.Id == userId)
            {
                throw new InvalidReplacementReviewerException();
            }

            var now = DateTimeOffset.UtcNow;
            var (callerUserId, callerServiceAccountId) = GetCallerIdentity();
            foreach (var task in pending)
            {
                task.State.AssignedToUserId = replacement.Id;
                task.State.UpdatedAt = now;
                task.State.ReminderSentAt = null; // fresh pre-deadline reminder for the new reviewer
                _dbContext.WorkflowTransitions.Add(new WorkflowTransition
                {
                    Id = Guid.NewGuid(),
                    TenantId = task.State.TenantId,
                    WorkflowStateId = task.State.Id,
                    FromStatus = WorkflowStatus.InReview,
                    ToStatus = WorkflowStatus.InReview,
                    AssignedToUserId = replacement.Id,
                    PerformedByUserId = callerUserId,
                    PerformedByServiceAccountId = callerServiceAccountId,
                    CreatedAt = now,
                });
            }
        }

        user.IsActive = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(AuditActions.UserDeactivated, "User", user.Id, user.DisplayName, cancellationToken: cancellationToken);

        if (replacement is not null)
        {
            foreach (var task in pending)
            {
                await _audit.RecordAsync(AuditActions.WorkflowReassigned, "Document", task.DocumentId, task.DocumentName,
                    $"reviewer: {replacement.DisplayName} (reassigned on deactivation of {user.DisplayName})", cancellationToken: cancellationToken);
                await _notifications.NotifyAsync(replacement.Id, NotificationType.ReviewAssigned, "Review requested",
                    $"You've been asked to review '{task.DocumentName}'.", task.DocumentId, cancellationToken);
            }
        }

        return NoContent();
    }

    // An action endpoint, mirrors POST /documents/{id}/restore — the reverse of Deactivate.
    [HttpPost("{userId:guid}/reactivate")]
    public async Task<IActionResult> Reactivate(Guid userId, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        var user = await _dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        user.IsActive = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(AuditActions.UserReactivated, "User", user.Id, user.DisplayName, cancellationToken: cancellationToken);

        return Ok(BuildResource(user));
    }

    // Sets the user's full system-rights bundle — see ADR "Users & groups administration tab". Gated on
    // the caller's own CanManageUsers, then capped by SystemRightsPolicy: the caller can only grant a right
    // it holds itself, and any change to IsTenantAdmin requires the caller be a tenant admin. Keyed as its
    // own sub-resource (like DocumentsController's .../mask) rather than folded into the narrow rename PUT.
    [HttpPut("{userId:guid}/rights")]
    public async Task<IActionResult> SetRights(Guid userId, [FromBody] SystemRights request, CancellationToken cancellationToken)
    {
        var caller = await GetCallerSystemRightsAsync(cancellationToken);

        if (!caller.CanManageUsers)
        {
            return Forbid();
        }

        var user = await _dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        if (!SystemRightsPolicy.CanApply(caller, Users.SystemRightsMapping.Read(user), request))
        {
            throw InsufficientRightsToGrantException.OnSystemRights();
        }

        Users.SystemRightsMapping.Apply(user, request);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(AuditActions.UserRightsChanged, "User", user.Id, user.DisplayName, Users.SystemRightsMapping.Describe(request), cancellationToken: cancellationToken);

        return Ok(BuildResource(user));
    }

    // Self-service — requires being logged in as a User (ICurrentUserAccessor.UserId set), not gated on
    // CanManageUsers. The one new endpoint this ADR adds outside the login mechanism itself: without it, a
    // User provisioned with an admin-set initial password could never rotate away from it. See ADR
    // "Interactive User login (foundation slice)".
    [HttpPut("me/password")]
    public async Task<IActionResult> ChangeOwnPassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var user = await _dbContext.Users.SingleAsync(u => u.Id == userId, cancellationToken);

        if (user.PasswordHash is null || _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            throw new InvalidCurrentPasswordException();
        }

        user.PasswordHash = _passwordHasher.HashPassword(user, request.NewPassword);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(AuditActions.UserPasswordChanged, "User", user.Id, user.DisplayName, cancellationToken: cancellationToken);

        return NoContent();
    }

    public class ResetPasswordResponse
    {
        public string Password { get; set; } = "";
    }

    // Admin password reset (ADR "User password management"): sets a fresh random password and returns it
    // once (no email/invite flow exists), for the admin to hand to the user, who then changes it via
    // PUT /users/me/password. Gated on CanManageUsers. An action endpoint (POST), like rotate-secret —
    // each call mints a new password. Same random shape as the TenantAdministrator initial password.
    [HttpPost("{userId:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid userId, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        var user = await _dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        user.PasswordHash = _passwordHasher.HashPassword(user, password);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(AuditActions.UserPasswordReset, "User", user.Id, user.DisplayName, cancellationToken: cancellationToken);

        return Ok(new ResetPasswordResponse { Password = password });
    }

    // ---- Two-factor authentication (ADR "MFA (interactive login, TOTP)") --------------------------------
    // Self-service TOTP enrollment: enroll generates a secret (stored but not yet active), enable confirms it
    // with a code and returns the one-time recovery codes, delete disables. An admin with CanResetMfa can
    // reset (disable) a locked-out user's MFA. All require being a logged-in User; a ServiceAccount has none.

    public class MfaEnrollResponse
    {
        public string Secret { get; set; } = "";

        public string OtpauthUri { get; set; } = "";

        // The enrollment QR as a data URL (image/png), so the client can render it directly.
        public string QrDataUrl { get; set; } = "";
    }

    public class MfaEnableRequest
    {
        public string Code { get; set; } = "";
    }

    public class MfaRecoveryCodesResponse
    {
        public List<string> RecoveryCodes { get; set; } = [];
    }

    // Generates a fresh secret and stores it as a pending (unconfirmed) enrollment — MfaEnabledAt stays null
    // until enable confirms a code, so a half-finished enrollment never blocks login. Re-enrolling overwrites
    // any prior pending/active secret.
    [HttpPost("me/mfa/enroll")]
    public async Task<IActionResult> EnrollMfa(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var user = await _dbContext.Users.SingleAsync(u => u.Id == userId, cancellationToken);

        var secret = _mfa.GenerateSecret();
        user.TotpSecret = await _transit.EncryptAsync(secret, cancellationToken); // encrypted at rest (OpenBao transit)
        user.MfaEnabledAt = null; // not active until confirmed
        await _dbContext.SaveChangesAsync(cancellationToken);

        var otpauth = _mfa.BuildOtpauthUri(secret, user.Email);
        var qr = Convert.ToBase64String(_mfa.GenerateQrPng(otpauth));

        return Ok(new MfaEnrollResponse
        {
            Secret = secret,
            OtpauthUri = otpauth,
            QrDataUrl = $"data:image/png;base64,{qr}",
        });
    }

    // Confirms enrollment: verifies a code against the pending secret, activates MFA, and (re)generates the
    // one-time recovery codes — returned once, only their hashes are stored.
    [HttpPost("me/mfa/enable")]
    public async Task<IActionResult> EnableMfa([FromBody] MfaEnableRequest request, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var user = await _dbContext.Users.SingleAsync(u => u.Id == userId, cancellationToken);

        if (user.TotpSecret is null)
        {
            throw new MfaNotEnrolledException();
        }

        if (!_mfa.VerifyTotp(await _transit.DecryptAsync(user.TotpSecret, cancellationToken), request.Code))
        {
            throw new InvalidMfaCodeException();
        }

        user.MfaEnabledAt = DateTimeOffset.UtcNow;

        // Replace any prior recovery codes with a fresh set.
        var existing = await _dbContext.UserRecoveryCodes.Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        _dbContext.UserRecoveryCodes.RemoveRange(existing);

        var codes = _mfa.GenerateRecoveryCodes();
        foreach (var (_, hash) in codes)
        {
            _dbContext.UserRecoveryCodes.Add(new UserRecoveryCode
            {
                Id = Guid.NewGuid(),
                TenantId = user.TenantId,
                UserId = userId,
                CodeHash = hash,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(AuditActions.UserMfaEnabled, "User", user.Id, user.DisplayName, cancellationToken: cancellationToken);

        return Ok(new MfaRecoveryCodesResponse { RecoveryCodes = codes.Select(c => c.Plaintext).ToList() });
    }

    // Self-disable — the caller already passed MFA at login this session, so possession is proven; no code is
    // re-required. Clears the secret + recovery codes. A no-op is fine if MFA wasn't on.
    [HttpDelete("me/mfa")]
    public async Task<IActionResult> DisableMfa(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var user = await _dbContext.Users.SingleAsync(u => u.Id == userId, cancellationToken);
        await ClearMfaAsync(user, cancellationToken);
        await _audit.RecordAsync(AuditActions.UserMfaDisabled, "User", user.Id, user.DisplayName, cancellationToken: cancellationToken);

        return NoContent();
    }

    // Admin reset for a locked-out user (lost authenticator). Gated on CanResetMfa — the dedicated right,
    // finally enforced here. Disables the target's MFA so they can log in with just their password and
    // re-enroll. Distinct from CanManageUsers on purpose (a help-desk role may reset MFA without full user
    // administration).
    [HttpPost("{userId:guid}/mfa/reset")]
    public async Task<IActionResult> ResetMfa(Guid userId, CancellationToken cancellationToken)
    {
        if (!await CanResetMfaAsync(cancellationToken))
        {
            return Forbid();
        }

        var user = await _dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        await ClearMfaAsync(user, cancellationToken);
        await _audit.RecordAsync(AuditActions.UserMfaReset, "User", user.Id, user.DisplayName, cancellationToken: cancellationToken);

        return NoContent();
    }

    private async Task ClearMfaAsync(User user, CancellationToken cancellationToken)
    {
        user.TotpSecret = null;
        user.MfaEnabledAt = null;
        var codes = await _dbContext.UserRecoveryCodes.Where(c => c.UserId == user.Id).ToListAsync(cancellationToken);
        _dbContext.UserRecoveryCodes.RemoveRange(codes);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    // ---- Profile photo (ADR "User profile photo") ------------------------------------------------------
    // The clients crop + normalize to a 256×256 PNG before upload; the raw PNG bytes are the request body
    // (Content-Type image/png). Admins set any user's photo (CanManageUsers); a user sets their own via the
    // me/photo routes. GET returns image/png (self or CanManageUsers); DELETE removes it.

    [HttpPut("{userId:guid}/photo")]
    public async Task<IActionResult> SetPhoto(Guid userId, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        return await StorePhotoAsync(userId, cancellationToken);
    }

    [HttpPut("me/photo")]
    public async Task<IActionResult> SetMyPhoto(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        return await StorePhotoAsync(userId, cancellationToken);
    }

    // Readable by ANY member of the tenant, not just the user themself or a CanManageUsers holder (ADR 0544).
    // A profile photo is how colleagues recognise each other in the chat thread's author card; gating it to
    // administrators made the card useless for exactly the people who read the thread. The tenant query filter
    // is the boundary — a userId from another tenant resolves to nothing and returns 404, not someone's face.
    [HttpGet("{userId:guid}/photo")]
    public async Task<IActionResult> GetPhoto(Guid userId, CancellationToken cancellationToken)
    {
        var photo = await _dbContext.UserProfilePhotos
            .Where(p => p.UserId == userId)
            .Select(p => p.Photo)
            .SingleOrDefaultAsync(cancellationToken);

        return photo is null ? NotFound() : File(photo, "image/png");
    }

    [HttpHead("{userId:guid}/photo")]
    public async Task<IActionResult> HeadPhoto(Guid userId, CancellationToken cancellationToken)
    {
        var exists = await _dbContext.UserProfilePhotos.AnyAsync(p => p.UserId == userId, cancellationToken);
        return exists ? NoContent() : NotFound();
    }

    // ---- Identity card (ADR 0544) ----------------------------------------------------------------------
    // The small "who is this?" card behind an author name in the chat thread (and, later, an @-mention): display
    // name, email, and whether a photo exists. Readable by ANY member of the tenant.
    //
    // Deliberately a SEPARATE resource from GET /api/users/{id}, which is the administrative user record and stays
    // gated on CanManageUsers. This one exposes only the three fields a card renders, so widening its audience to
    // the whole tenant does not also widen access to rights flags, MFA state or activation status.
    //
    // Tenant isolation needs no explicit check: the Users query filter scopes the lookup, so an id from another
    // tenant is simply not found.
    [HttpGet("{userId:guid}/card")]
    public async Task<IActionResult> GetCard(Guid userId, CancellationToken cancellationToken)
    {
        var card = await _dbContext.Users
            .Where(u => u.Id == userId)
            .Select(u => new UserCardResource
            {
                UserId = u.Id,
                DisplayName = u.DisplayName,
                Email = u.Email,
                IsActive = u.IsActive,
                HasPhoto = _dbContext.UserProfilePhotos.Any(p => p.UserId == u.Id),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (card is null)
        {
            return NotFound();
        }

        card.Links =
        [
            new Link("self", $"/api/users/{userId}/card", "GET"),
            // Only advertised when there is one to fetch, so a client follows the rel rather than probing for a
            // 404 — absence of the rel is the answer (ADR 0543).
            .. card.HasPhoto ? new[] { new Link("photo", $"/api/users/{userId}/photo", "GET") } : [],
        ];

        return Ok(card);
    }

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead("{userId:guid}/card")]
    public async Task<IActionResult> HeadCard(Guid userId, CancellationToken cancellationToken) =>
        await _dbContext.Users.AnyAsync(u => u.Id == userId, cancellationToken) ? NoContent() : NotFound();

    [HttpDelete("{userId:guid}/photo")]
    public async Task<IActionResult> DeletePhoto(Guid userId, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        return await RemovePhotoAsync(userId, cancellationToken);
    }

    [HttpDelete("me/photo")]
    public async Task<IActionResult> DeleteMyPhoto(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        return await RemovePhotoAsync(userId, cancellationToken);
    }

    private async Task<IActionResult> StorePhotoAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (Request.ContentLength > ProfilePhotoValidator.MaxBytes)
        {
            throw new InvalidProfilePhotoException();
        }

        var user = await _dbContext.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.TenantId })
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        if (!ProfilePhotoValidator.IsValid(bytes, out var error))
        {
            throw new InvalidProfilePhotoException(error!);
        }

        var photo = await _dbContext.UserProfilePhotos.SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (photo is null)
        {
            _dbContext.UserProfilePhotos.Add(new UserProfilePhoto
            {
                UserId = userId,
                TenantId = user.TenantId,
                Photo = bytes,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            photo.Photo = bytes;
            photo.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<IActionResult> RemovePhotoAsync(Guid userId, CancellationToken cancellationToken)
    {
        var photo = await _dbContext.UserProfilePhotos.SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (photo is null)
        {
            return NotFound();
        }

        _dbContext.UserProfilePhotos.Remove(photo);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static UserResource BuildResource(User user)
    {
        return new UserResource
        {
            Id = user.Id,
            Email = user.Email,
            DisplayName = user.DisplayName,
            IsActive = user.IsActive,
            MfaEnabled = user.MfaEnabledAt is not null,
            Rights = Users.SystemRightsMapping.Read(user),
            Links =
            [
                new Link("self", $"/api/users/{user.Id}", "GET"),
                new Link("rights", $"/api/users/{user.Id}/rights", "PUT"),
                // Setting this user's avatar — the admin counterpart of the me resource's own `photo` rel
                // (issue #416). The list is already CanManageUsers-gated, which is the same right the PUT
                // enforces, so anyone holding this row may use it. Its absence elsewhere is what kept the
                // profile-photo dialog composing /users/{id}/photo for the admin case.
                new Link("photo", $"/api/users/{user.Id}/photo", "PUT"),
                // The remaining administrative actions on this user (issue #416). All are gated by the same
                // CanManageUsers right that gates the listing itself, so anyone holding this row may use them —
                // which is why they are unconditional here rather than recomputed per row.
                new Link("reset-password", $"/api/users/{user.Id}/reset-password", "POST"),
                new Link("reset-mfa", $"/api/users/{user.Id}/mfa/reset", "POST"),
                new Link("deactivate", $"/api/users/{user.Id}", "DELETE"),
            ],
        };
    }


    // The calling principal's identity, for attributing a workflow reassignment transition (exactly one of the
    // two is set — the two accessors are mutually exclusive per request, CurrentPrincipalMiddleware).
    private (Guid? UserId, Guid? ServiceAccountId) GetCallerIdentity() =>
        _currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId
            ? (null, serviceAccountId)
            : (_currentUserAccessor.UserId, null);

    // The caller's own system rights, for the escalation cap — a ServiceAccount only carries the four
    // management rights (no IsTenantAdmin/impersonate/etc.), a User carries all ten.
    private async Task<SystemRights> GetCallerSystemRightsAsync(CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            var sa = await _dbContext.ServiceAccounts
                .Where(s => s.Id == serviceAccountId)
                .Select(s => new { s.CanManageRepositories, s.CanManageMasks, s.CanManageServiceAccounts, s.CanManageUsers, s.CanViewAuditLog, s.CanExport, s.CanImport, s.CanManageIntrayes })
                .SingleAsync(cancellationToken);

            var caller = new SystemRights
            {
                CanManageRepositories = sa.CanManageRepositories,
                CanManageMasks = sa.CanManageMasks,
                CanManageServiceAccounts = sa.CanManageServiceAccounts,
                CanManageUsers = sa.CanManageUsers,
                CanViewAuditLog = sa.CanViewAuditLog,
                CanExport = sa.CanExport,
                CanImport = sa.CanImport,
                CanManageIntrayes = sa.CanManageIntrayes,
            };
            caller.ClearanceRank = (await _clearanceResolver.GetForServiceAccountAsync(serviceAccountId, cancellationToken)).Rank;
            return caller;
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            // Effective rights (own ∪ groups) so the escalation cap counts rights held via a group as
            // grantable — ADR "Enforce group system rights for members". Clearance likewise is the caller's
            // effective clearance (own ⊔ groups), so a clearance held via a group can be handed out.
            var caller = ToApiRights(await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken));
            caller.ClearanceRank = (await _clearanceResolver.GetForUserAsync(userId, cancellationToken)).Rank;
            return caller;
        }

        return new SystemRights();
    }

    private static SystemRights ToApiRights(SystemRightsSet r) => new()
    {
        IsTenantAdmin = r.IsTenantAdmin,
        CanImpersonate = r.CanImpersonate,
        CanOverrideCheckout = r.CanOverrideCheckout,
        CanLegalHold = r.CanLegalHold,
        CanManageClassification = r.CanManageClassification,
        CanResetMfa = r.CanResetMfa,
        CanManageRepositories = r.CanManageRepositories,
        CanManageMasks = r.CanManageMasks,
        CanManageServiceAccounts = r.CanManageServiceAccounts,
        CanManageUsers = r.CanManageUsers,
        CanViewAuditLog = r.CanViewAuditLog,
        CanExport = r.CanExport,
        CanImport = r.CanImport,
        CanManageIntrayes = r.CanManageIntrayes,
        CanCreateExternalLink = r.CanCreateExternalLink,
    };

    // Checks ServiceAccount.CanManageUsers first, then User.CanManageUsers — see ADR "User support for
    // ServiceAccount/User/Group/Mask management endpoints".
    private async Task<bool> CanManageUsersAsync(CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await _dbContext.ServiceAccounts
                .Where(s => s.Id == serviceAccountId)
                .Select(s => s.CanManageUsers)
                .SingleAsync(cancellationToken);
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            // Effective rights (own ∪ groups) so CanManageUsers held via a group takes effect — ADR
            // "Enforce group system rights for members".
            return (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanManageUsers;
        }

        return false;
    }

    // CanResetMfa is a User-only right (a ServiceAccount has no equivalent), so a ServiceAccount caller can't
    // reset MFA. For a User caller it's the effective right (own ∪ groups) — ADR "MFA (interactive login,
    // TOTP)" / "Enforce group system rights for members".
    private async Task<bool> CanResetMfaAsync(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanResetMfa;
        }

        return false;
    }
}
