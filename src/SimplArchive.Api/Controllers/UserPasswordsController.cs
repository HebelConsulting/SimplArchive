using System.Security.Cryptography;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Principals;
using SimplArchive.Api.Principals;
using SimplArchive.Application.Abstractions;
using SimplArchive.Application.Security;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Setting a user's password — their own, or an administrator's reset.
/// </summary>
/// <remarks>
/// <para>
/// <b>A sibling controller on the same routes</b> (ADR 0571's recipe, the one that brought
/// <c>DocumentsController</c> back from 2,613 lines). Password handling is a cohesive responsibility and it is
/// the part that keeps growing: adding the composition policy (#849) to <c>UsersController</c> took it from 997
/// to 1011 lines, over the limit — which is the rule working rather than an obstacle, since the file was full.
/// </para>
/// <para>
/// <b>The routes are unchanged</b>, which is the whole point of splitting by responsibility rather than by
/// address: <c>PUT /api/users/me/password</c> and <c>POST /api/users/{userId}/reset-password</c> are where they
/// always were, so no client and no rel moves (ADR 0543 — rel names are the compatibility surface, and these
/// are reached from the <c>me</c> resource exactly as before).
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/users")]
[Authorize]
public class UserPasswordsController : ControllerBase
{
    public class ChangePasswordRequest
    {
        public string CurrentPassword { get; set; } = string.Empty;

        public string NewPassword { get; set; } = string.Empty;
    }

    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _users_accessor;
    private readonly UserAccessService _access;
    private readonly IAuditRecorder _audit;
    private readonly Concurrency.UserVerbs _verbs;
    private readonly PasswordHasher<User> _passwordHasher = new();

    public UserPasswordsController(
        SimplArchiveDbContext dbContext,
        ICurrentUserAccessor users,
        UserAccessService access,
        IAuditRecorder audit,
        Concurrency.UserVerbs verbs)
    {
        _dbContext = dbContext;
        _users_accessor = users;
        _access = access;
        _audit = audit;
        _verbs = verbs;
    }

    // Self-service — requires being logged in as a User (ICurrentUserAccessor.UserId set), not gated on
    // CanManageUsers. The one new endpoint this ADR adds outside the login mechanism itself: without it, a
    // User provisioned with an admin-set initial password could never rotate away from it. See ADR
    // "Interactive User login (foundation slice)".
    [HttpPut("me/password")]
    public async Task<IActionResult> ChangeOwnPassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (_users_accessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var user = await _dbContext.Users.SingleAsync(u => u.Id == userId, cancellationToken);

        if (user.PasswordHash is null || _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            throw new InvalidCurrentPasswordException();
        }

        if (PasswordPolicy.Refusal(request.NewPassword) is { } refusal)
        {
            throw new PasswordRefusedException(refusal);
        }

        user.PasswordHash = _passwordHasher.HashPassword(user, request.NewPassword);
        await _verbs.MutateAsync(Request, user, apply: () => Task.CompletedTask, cancellationToken: cancellationToken);
        await _audit.RecordAsync(AuditActions.UserPasswordChanged, "User", user.Id, user.DisplayName, cancellationToken: cancellationToken);

        return NoContent();
    }

    public class ResetPasswordResponse
    {
        public string Password { get; set; } = string.Empty;
    }

    // Admin password reset (ADR "User password management"): sets a fresh random password and returns it
    // once (no email/invite flow exists), for the admin to hand to the user, who then changes it via
    // PUT /users/me/password. Gated on CanManageUsers. An action endpoint (POST), like rotate-secret —
    // each call mints a new password. Same random shape as the TenantAdministrator initial password.
    [HttpPost("{userId:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid userId, CancellationToken cancellationToken)
    {
        if (!await _access.CanManageUsersAsync(cancellationToken))
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
        await _verbs.MutateAsync(Request, user, apply: () => Task.CompletedTask, cancellationToken: cancellationToken);
        await _audit.RecordAsync(AuditActions.UserPasswordReset, "User", user.Id, user.DisplayName, cancellationToken: cancellationToken);

        return Ok(new ResetPasswordResponse { Password = password });
    }
}
