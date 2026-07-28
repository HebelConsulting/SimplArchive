using System.Security.Claims;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using SimplArchive.Api.Controllers;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Pages.Account;

/// <summary>
/// Server-rendered passkey management (ADR "Desktop passkey management"). A native Avalonia window can't run
/// a WebAuthn attestation ceremony (there's no <c>navigator.credentials</c>), so the desktop client delegates
/// passkey <em>registration</em> to the system browser on this page — mirroring how it already delegates
/// login to the browser via a loopback redirect. The page authenticates against the cookie scheme (the same
/// session the desktop's browser login established), runs the Fido2 ceremony server-side (like the login
/// page's assertion), and — when opened with a <c>?loopback=&lt;port&gt;</c> from the desktop — redirects to
/// <c>http://127.0.0.1:&lt;port&gt;/passkey-done</c> on success so the desktop auto-refreshes. Opened directly
/// (no loopback) it works as a standalone list/add/remove page.
/// </summary>
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
public class PasskeysModel : PageModel
{
    private static readonly TimeSpan OptionsLifetime = TimeSpan.FromMinutes(5);

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IFido2 _fido2;
    private readonly IAuditRecorder _audit;
    private readonly ITimeLimitedDataProtector _optionsProtector;

    public PasskeysModel(SimplArchiveDbContext dbContext, IFido2 fido2, IAuditRecorder audit, IDataProtectionProvider dataProtection)
    {
        _dbContext = dbContext;
        _fido2 = fido2;
        _audit = audit;
        // Same purpose as PasskeysController's register token — an independent use of the same ceremony.
        _optionsProtector = dataProtection.CreateProtector("SimplArchive.PasskeyRegister").ToTimeLimitedDataProtector();
    }

    // The loopback port the desktop client listens on; when present, a successful add redirects there so the
    // desktop can close the browser flow and refresh. Absent when the page is opened directly in a browser.
    [BindProperty(SupportsGet = true)]
    public int? Loopback { get; set; }

    [BindProperty]
    public string NewName { get; set; } = "";

    [BindProperty]
    public string PasskeyToken { get; set; } = "";

    [BindProperty]
    public string PasskeyResponse { get; set; } = "";

    public string? Error { get; set; }
    public string? Notice { get; set; }

    // Fresh registration options + the signed token binding an attestation to them (per render).
    public string? RegisterOptionsJson { get; private set; }

    public List<PasskeyRow> Passkeys { get; private set; } = [];

    public record PasskeyRow(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRegisterAsync(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (userId is null)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(NewName))
        {
            Error = "A name for the passkey is required.";
            await LoadAsync(cancellationToken);
            return Page();
        }

        CredentialCreateOptions options;
        try
        {
            options = CredentialCreateOptions.FromJson(_optionsProtector.Unprotect(PasskeyToken));
        }
        catch (Exception)
        {
            Error = "The registration challenge expired. Please try again.";
            await LoadAsync(cancellationToken);
            return Page();
        }

        AuthenticatorAttestationRawResponse? attestation;
        try
        {
            attestation = System.Text.Json.JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(PasskeyResponse);
        }
        catch (Exception)
        {
            attestation = null;
        }

        if (attestation is null)
        {
            Error = "The attestation response is invalid.";
            await LoadAsync(cancellationToken);
            return Page();
        }

        RegisteredPublicKeyCredential credential;
        try
        {
            credential = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = attestation,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = async (args, ct) =>
                    !await _dbContext.WebAuthnCredentials.IgnoreQueryFilters(["TenantFilter"]).AnyAsync(c => c.CredentialId == args.CredentialId, ct),
            }, cancellationToken);
        }
        catch (Fido2VerificationException)
        {
            Error = "The passkey could not be verified. Please try again.";
            await LoadAsync(cancellationToken);
            return Page();
        }

        var user = await _dbContext.Users.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(u => u.Id == userId.Value, cancellationToken);
        _dbContext.WebAuthnCredentials.Add(new WebAuthnCredential
        {
            Id = Guid.NewGuid(),
            TenantId = user.TenantId,
            UserId = userId.Value,
            CredentialId = credential.Id,
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            AaGuid = credential.AaGuid,
            Transports = credential.Transports is { Length: > 0 } t ? string.Join(',', t) : null,
            Name = NewName.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _dbContext.SaveChangesAsync(cancellationToken);

        // The current-principal accessors aren't populated on a cookie-authenticated Razor page, so the actor
        // is supplied explicitly — same as the login page's audit call.
        await _audit.RecordForActorAsync(AuditActorType.User, user.Id, user.DisplayName, user.TenantId,
            AuditActions.PasskeyRegistered, "User", user.Id, NewName.Trim());

        // When launched from the desktop, hand back to the loopback so it can refresh + close the browser flow.
        if (Loopback is { } port && port is > 0 and <= 65535)
        {
            return Redirect($"http://127.0.0.1:{port}/passkey-done?added=1");
        }

        Notice = $"Passkey “{NewName.Trim()}” added.";
        NewName = "";
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (userId is null)
        {
            return Forbid();
        }

        var passkey = await _dbContext.WebAuthnCredentials.IgnoreQueryFilters(["TenantFilter"]).SingleOrDefaultAsync(c => c.Id == id && c.UserId == userId.Value, cancellationToken);
        if (passkey is not null)
        {
            var user = await _dbContext.Users.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(u => u.Id == userId.Value, cancellationToken);
            _dbContext.WebAuthnCredentials.Remove(passkey);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _audit.RecordForActorAsync(AuditActorType.User, user.Id, user.DisplayName, user.TenantId,
                AuditActions.PasskeyRemoved, "User", user.Id, passkey.Name);
            Notice = $"Passkey “{passkey.Name}” removed.";
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    private Guid? CurrentUserId() =>
        Guid.TryParse(User.FindFirstValue(OpenIddictConstants.Claims.Subject), out var id) ? id : null;

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (userId is null)
        {
            return;
        }

        var user = await _dbContext.Users.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(u => u.Id == userId.Value, cancellationToken);

        // No ambient tenant on a cookie-authenticated Razor page, so bypass the tenant filter and resolve by
        // the (global) UserId from the cookie subject — same as the Users lookups above.
        Passkeys = await _dbContext.WebAuthnCredentials
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(c => c.UserId == userId.Value)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new PasskeyRow(c.Id, c.Name, c.CreatedAt, c.LastUsedAt))
            .ToListAsync(cancellationToken);

        var existing = await _dbContext.WebAuthnCredentials
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(c => c.UserId == userId.Value)
            .Select(c => c.CredentialId)
            .ToListAsync(cancellationToken);

        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = userId.Value.ToByteArray(), Name = user.Email, DisplayName = user.DisplayName },
            ExcludeCredentials = existing.Select(cid => new PublicKeyCredentialDescriptor(cid)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        RegisterOptionsJson = options.ToJson();
        PasskeyToken = _optionsProtector.Protect(options.ToJson(), OptionsLifetime);
    }
}
