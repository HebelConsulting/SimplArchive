using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Authorization;
using SimplArchive.Api.Errors.Exceptions.Principals;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// TOTP two-factor enrollment and reset, on the <c>api/users</c> routes (ADR "MFA (interactive login, TOTP)").
/// </summary>
/// <remarks>
/// A SIBLING controller on the same route prefix rather than more of <see cref="UsersController"/> — the shape
/// ADR 0571 established when DocumentsController came down from 2,613. MFA is a self-contained surface: four
/// actions, its own DTOs, and it shares nothing with user CRUD but the tenant and the caller.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/users")]
[Authorize]
public class UserMfaController(
    SimplArchiveDbContext dbContext,
    ICurrentUserAccessor currentUserAccessor,
    IUserSystemRightsResolver userSystemRights,
    IAuditRecorder audit,
    ITransitEncryptor transit,
    Authentication.MfaService mfa) : ControllerBase
{
    // ---- Two-factor authentication (ADR "MFA (interactive login, TOTP)") --------------------------------
    // Self-service TOTP enrollment: enroll generates a secret (stored but not yet active), enable confirms it
    // with a code and returns the one-time recovery codes, delete disables. An admin with CanResetMfa can
    // reset (disable) a locked-out user's MFA. All require being a logged-in User; a ServiceAccount has none.

    public class MfaEnrollResponse
    {
        public string Secret { get; set; } = string.Empty;

        public string OtpauthUri { get; set; } = string.Empty;

        // The enrollment QR as a data URL (image/png), so the client can render it directly.
        public string QrDataUrl { get; set; } = string.Empty;
    }

    public class MfaEnableRequest
    {
        public string Code { get; set; } = string.Empty;
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
        if (currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var user = await dbContext.Users.SingleAsync(u => u.Id == userId, cancellationToken);

        var secret = mfa.GenerateSecret();
        user.TotpSecret = await transit.EncryptAsync(secret, cancellationToken); // encrypted at rest (OpenBao transit)
        user.MfaEnabledAt = null; // not active until confirmed
        await dbContext.SaveChangesAsync(cancellationToken);

        var otpauth = mfa.BuildOtpauthUri(secret, user.Email);
        var qr = Convert.ToBase64String(mfa.GenerateQrPng(otpauth));

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
        if (currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var user = await dbContext.Users.SingleAsync(u => u.Id == userId, cancellationToken);

        if (user.TotpSecret is null)
        {
            throw new MfaNotEnrolledException();
        }

        if (!mfa.VerifyTotp(await transit.DecryptAsync(user.TotpSecret, cancellationToken), request.Code))
        {
            throw new InvalidMfaCodeException();
        }

        user.MfaEnabledAt = DateTimeOffset.UtcNow;

        // Replace any prior recovery codes with a fresh set.
        var existing = await dbContext.UserRecoveryCodes.Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        dbContext.UserRecoveryCodes.RemoveRange(existing);

        var codes = mfa.GenerateRecoveryCodes();
        foreach (var (_, hash) in codes)
        {
            dbContext.UserRecoveryCodes.Add(new UserRecoveryCode
            {
                Id = Guid.NewGuid(),
                TenantId = user.TenantId,
                UserId = userId,
                CodeHash = hash,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(AuditActions.UserMfaEnabled, "User", user.Id, user.DisplayName, cancellationToken: cancellationToken);

        return Ok(new MfaRecoveryCodesResponse { RecoveryCodes = codes.Select(c => c.Plaintext).ToList() });
    }

    // Self-disable — the caller already passed MFA at login this session, so possession is proven; no code is
    // re-required. Clears the secret + recovery codes. A no-op is fine if MFA wasn't on.
    [HttpDelete("me/mfa")]
    public async Task<IActionResult> DisableMfa(CancellationToken cancellationToken)
    {
        if (currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var user = await dbContext.Users.SingleAsync(u => u.Id == userId, cancellationToken);
        await ClearMfaAsync(user, cancellationToken);
        await audit.RecordAsync(AuditActions.UserMfaDisabled, "User", user.Id, user.DisplayName, cancellationToken: cancellationToken);

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

        var user = await dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        await ClearMfaAsync(user, cancellationToken);
        await audit.RecordAsync(AuditActions.UserMfaReset, "User", user.Id, user.DisplayName, cancellationToken: cancellationToken);

        return NoContent();
    }

    private async Task ClearMfaAsync(User user, CancellationToken cancellationToken)
    {
        user.TotpSecret = null;
        user.MfaEnabledAt = null;
        var codes = await dbContext.UserRecoveryCodes.Where(c => c.UserId == user.Id).ToListAsync(cancellationToken);
        dbContext.UserRecoveryCodes.RemoveRange(codes);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // CanResetMfa is a User-only right (a ServiceAccount has no equivalent), so a ServiceAccount caller can't
    // reset MFA. For a User caller it's the effective right (own ∪ groups) — ADR "MFA (interactive login,
    // TOTP)" / "Enforce group system rights for members".
    private async Task<bool> CanResetMfaAsync(CancellationToken cancellationToken)
    {
        if (currentUserAccessor.UserId is { } userId)
        {
            return (await userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanResetMfa;
        }

        return false;
    }
}
