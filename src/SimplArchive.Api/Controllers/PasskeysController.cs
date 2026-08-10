using System.Text.Json;
using Asp.Versioning;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Passkeys;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Self-service WebAuthn/passkey registration + management for the logged-in User (ADR "WebAuthn passkeys as a
/// second factor"). Registration runs the browser attestation ceremony: the client fetches options + a signed
/// options token, calls navigator.credentials.create, and posts the attestation back with the token. The
/// passkey is then usable as a second factor at the login challenge (see AccountController's assertion endpoints).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/users/me/passkeys")]
[Authorize]
public class PasskeysController : ControllerBase
{
    // The options token binds a register attestation to the options this server issued; short-lived.
    private static readonly TimeSpan OptionsLifetime = TimeSpan.FromMinutes(5);

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IFido2 _fido2;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly IAuditRecorder _audit;
    private readonly ITimeLimitedDataProtector _optionsProtector;

    public PasskeysController(
        SimplArchiveDbContext dbContext,
        IFido2 fido2,
        ICurrentUserAccessor currentUserAccessor,
        ICurrentTenantAccessor currentTenantAccessor,
        IAuditRecorder audit,
        IDataProtectionProvider dataProtection)
    {
        _dbContext = dbContext;
        _fido2 = fido2;
        _currentUserAccessor = currentUserAccessor;
        _currentTenantAccessor = currentTenantAccessor;
        _audit = audit;
        _optionsProtector = dataProtection.CreateProtector("SimplArchive.PasskeyRegister").ToTimeLimitedDataProtector();
    }

    // ---- List / remove --------------------------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var passkeys = await _dbContext.WebAuthnCredentials
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new PasskeyResource { Id = c.Id, Name = c.Name, CreatedAt = c.CreatedAt, LastUsedAt = c.LastUsedAt })
            .ToListAsync(cancellationToken);

        // Each passkey addresses itself, so removing one is a rel to follow rather than a path both clients
        // rebuild from an id (issue #416).
        foreach (var passkey in passkeys)
        {
            passkey.Links = [new Link("self", $"/api/users/me/passkeys/{passkey.Id}", "DELETE")];
        }

        return Ok(new PasskeyListResource { Passkeys = passkeys, Links = [new Link("self", "/api/users/me/passkeys", "GET")] });
    }

    [HttpHead]
    public IActionResult Head() => _currentUserAccessor.UserId is null ? Forbid() : NoContent();

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var passkey = await _dbContext.WebAuthnCredentials.SingleOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken);
        if (passkey is null)
        {
            return NotFound();
        }

        _dbContext.WebAuthnCredentials.Remove(passkey);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(AuditActions.PasskeyRemoved, "User", userId, passkey.Name, cancellationToken: cancellationToken);

        return NoContent();
    }

    // ---- Registration ceremony ------------------------------------------------------------------------

    [HttpPost("register/options")]
    public async Task<IActionResult> RegisterOptions(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var user = await _dbContext.Users.SingleAsync(u => u.Id == userId, cancellationToken);
        var existing = await _dbContext.WebAuthnCredentials
            .Where(c => c.UserId == userId)
            .Select(c => c.CredentialId)
            .ToListAsync(cancellationToken);

        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = userId.ToByteArray(), Name = user.Email, DisplayName = user.DisplayName },
            ExcludeCredentials = existing.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var token = _optionsProtector.Protect(options.ToJson(), OptionsLifetime);
        return Ok(new { options = options.ToJson(), token });
    }

    public class RegisterRequest
    {
        // The navigator.credentials.create() result, serialized as JSON.
        public JsonElement AttestationResponse { get; set; }
        public string Token { get; set; } = "";
        public string Name { get; set; } = "";
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new PasskeyNameRequiredException();
        }

        CredentialCreateOptions options;
        try
        {
            options = CredentialCreateOptions.FromJson(_optionsProtector.Unprotect(request.Token));
        }
        catch (Exception)
        {
            throw new PasskeyChallengeExpiredException();
        }

        var attestation = request.AttestationResponse.Deserialize<AuthenticatorAttestationRawResponse>()
            ?? throw new PasskeyInvalidException();

        RegisteredPublicKeyCredential credential;
        try
        {
            credential = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = attestation,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = async (args, ct) =>
                    !await _dbContext.WebAuthnCredentials.AnyAsync(c => c.CredentialId == args.CredentialId, ct),
            }, cancellationToken);
        }
        catch (Fido2VerificationException e)
        {
            throw new PasskeyVerificationFailedException(e.Message);
        }

        _dbContext.WebAuthnCredentials.Add(new WebAuthnCredential
        {
            Id = Guid.NewGuid(),
            TenantId = _currentTenantAccessor.TenantId!.Value,
            UserId = userId,
            CredentialId = credential.Id,
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            AaGuid = credential.AaGuid,
            Transports = credential.Transports is { Length: > 0 } t ? string.Join(',', t) : null,
            Name = request.Name.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(AuditActions.PasskeyRegistered, "User", userId, request.Name.Trim(), cancellationToken: cancellationToken);

        return Ok(new { name = request.Name.Trim() });
    }

    public class PasskeyListResource : HypermediaResource
    {
        public List<PasskeyResource> Passkeys { get; set; } = [];
    }

    public class PasskeyResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? LastUsedAt { get; set; }
    }
}
