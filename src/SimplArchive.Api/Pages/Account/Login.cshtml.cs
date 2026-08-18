using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using SimplArchive.Api.Authentication;
using SimplArchive.Api.Controllers;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Pages.Account;

/// <summary>
/// The standard, well-trodden OpenIddict sample shape for a redirect-based Authorization Code flow — a
/// server-rendered login form, since a Blazor WASM SPA can't easily be the redirect target the flow
/// needs. See ADR "Interactive User login (foundation slice)". On success, signs in against the cookie
/// scheme (not the OpenIddict scheme — that's issued later, by AuthorizationController) and redirects
/// back to the original ~/connect/authorize request.
///
/// When the user has MFA enabled (ADR "MFA (interactive login, TOTP)"), a correct password does NOT sign
/// them in; instead it issues a short-lived, Data-Protection-signed ticket and renders a second step that
/// asks for a TOTP or recovery code. The ticket binds step 2 to a successful step 1, so the second step
/// can't be reached (and TOTP can't be brute-forced) without the password. Both clients share this page,
/// so the challenge covers the web and desktop apps alike.
/// </summary>
public class LoginModel : PageModel
{
    // The ticket proves "password verified for this user" between the two steps; short-lived to bound TOTP
    // guessing to a small window.
    private static readonly TimeSpan MfaTicketLifetime = TimeSpan.FromMinutes(5);

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IAuditRecorder _audit;
    private readonly MfaService _mfa;
    private readonly ITransitEncryptor _transit;
    private readonly Fido2NetLib.IFido2 _fido2;
    private readonly ITimeLimitedDataProtector _ticketProtector;
    // A separate protector for the require-MFA inline-enrolment tickets (carries the pending secret / a
    // post-enrolment continue), kept distinct from the login-challenge ticket purpose.
    private readonly ITimeLimitedDataProtector _enrollProtector;
    // Carries the passkey assertion options between the challenge render and the passkey verify.
    private readonly ITimeLimitedDataProtector _passkeyProtector;
    // Carries the *discoverable* (usernameless) passkey-login options — no user bound, unlike _passkeyProtector.
    private readonly ITimeLimitedDataProtector _passkeyLoginlessProtector;
    private readonly ILogger<LoginModel> _logger;
    private readonly PasswordHasher<User> _passwordHasher = new();

    public LoginModel(SimplArchiveDbContext dbContext, IAuditRecorder audit, MfaService mfa, ITransitEncryptor transit, Fido2NetLib.IFido2 fido2, IDataProtectionProvider dataProtection, ILogger<LoginModel> logger)
    {
        _dbContext = dbContext;
        _audit = audit;
        _mfa = mfa;
        _transit = transit;
        _fido2 = fido2;
        _logger = logger;
        _ticketProtector = dataProtection.CreateProtector("SimplArchive.MfaLogin").ToTimeLimitedDataProtector();
        _enrollProtector = dataProtection.CreateProtector("SimplArchive.MfaEnroll").ToTimeLimitedDataProtector();
        _passkeyProtector = dataProtection.CreateProtector("SimplArchive.PasskeyLogin").ToTimeLimitedDataProtector();
        _passkeyLoginlessProtector = dataProtection.CreateProtector("SimplArchive.PasskeyLoginless").ToTimeLimitedDataProtector();
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    // The MFA (second) step's fields.
    [BindProperty]
    public string? Code { get; set; }

    [BindProperty]
    public string? MfaTicket { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Error { get; set; }

    // Drives the view: false = ask for email/password, true = ask for the MFA code.
    public bool ShowMfa { get; set; }

    // The MFA step shows a TOTP field when the user has TOTP enabled, and/or a passkey button when they have a
    // registered passkey (ADR "WebAuthn passkeys as a second factor").
    public bool ShowTotp { get; set; }
    public bool ShowPasskey { get; set; }
    public string? PasskeyOptionsJson { get; set; }

    [BindProperty]
    public string? PasskeyToken { get; set; }

    [BindProperty]
    public string? PasskeyResponse { get; set; }

    // Require-MFA inline enrolment (ADR "MFA require-policy + TOTP secret encryption").
    [BindProperty]
    public string? EnrollTicket { get; set; }

    public bool ShowEnroll { get; set; }
    public string? EnrollQrDataUrl { get; set; }
    public string? EnrollSecret { get; set; }

    [BindProperty]
    public string? ContinueTicket { get; set; }

    public bool ShowRecoveryCodes { get; set; }
    public IReadOnlyList<string> RecoveryCodes { get; set; } = [];

    // Passwordless (discoverable) passkey login on the password step (ADR "Passwordless passkey login"). The
    // options are usernameless (empty allowCredentials); the assertion's user handle identifies the user.
    public string? PasskeyLoginOptionsJson { get; set; }

    [BindProperty]
    public string? PasskeyLoginToken { get; set; }

    [BindProperty]
    public string? PasskeyLoginResponse { get; set; }

    public class InputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = "";

        [Required]
        public string Password { get; set; } = "";
    }

    public void OnGet()
    {
        // Pre-fill the email from the OIDC login_hint carried in the original authorize request (inside
        // ReturnUrl), so a returning desktop user doesn't retype it (ADR "Browser-only desktop login +
        // login_hint"). The user can still edit it before signing in.
        if (string.IsNullOrEmpty(Input.Email) && TryGetLoginHint(ReturnUrl) is { } hint)
        {
            Input.Email = hint;
        }

        PreparePasskeyLoginOption();
    }

    // ReturnUrl is a local path+query like "/connect/authorize?…&login_hint=you%40example.com"; extract the hint.
    private static string? TryGetLoginHint(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl))
        {
            return null;
        }

        var q = returnUrl.IndexOf('?');
        if (q < 0)
        {
            return null;
        }

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(returnUrl[q..]);
        return query.TryGetValue("login_hint", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : null;
    }

    // Generates the usernameless (discoverable) assertion options for the "Sign in with a passkey" button on the
    // password step — a fresh challenge each render, carried to the verify via a signed token (no user bound;
    // the assertion's user handle identifies the user). UV required, since the passkey is the sole factor.
    private void PreparePasskeyLoginOption()
    {
        var options = _fido2.GetAssertionOptions(new Fido2NetLib.GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = Fido2NetLib.Objects.UserVerificationRequirement.Required,
        });
        PasskeyLoginOptionsJson = options.ToJson();
        PasskeyLoginToken = _passkeyLoginlessProtector.Protect(options.ToJson(), MfaTicketLifetime);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var normalizedEmail = Input.Email.ToUpperInvariant();

        // IgnoreQueryFilters(["TenantFilter"]) — no tenant is known yet at login time (this request is
        // fully anonymous), so the automatic tenant filter's TenantId == null predicate would otherwise
        // exclude every real row — same bug class as WellKnownMaskSeeder's own fix, ADR "Tenant onboarding
        // and platform-admin mechanism". (TenantId, NormalizedEmail) is only unique *within* a tenant
        // (ADR "Email case-sensitivity normalization"), so two different tenants could in principle share
        // the same email — a known, unresolved limitation of this foundation slice (no tenant selector on
        // the login form yet); treated the same as any other failed login rather than a 500.
        var matches = await _dbContext.Users
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(u => u.NormalizedEmail == normalizedEmail)
            .ToListAsync();

        var user = matches.Count == 1 ? matches[0] : null;

        if (user is null || user.PasswordHash is null || !user.IsActive)
        {
            // A failed login is a security signal — Warning so a SIEM can aggregate repeated failures into a
            // brute-force alert (ADR "Enterprise-grade structured logging with Serilog"). Never log the password.
            _logger.LogWarning("Failed login for {Email}: no active credentialed user", normalizedEmail);
            Error = SimplArchive.Localization.Strings.Get("LoginErrInvalidCreds");
            PreparePasskeyLoginOption();

            return Page();
        }

        var verification = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, Input.Password);

        if (verification == PasswordVerificationResult.Failed)
        {
            _logger.LogWarning("Failed login for user {UserId}: incorrect password", user.Id);
            Error = SimplArchive.Localization.Strings.Get("LoginErrInvalidCreds");
            PreparePasskeyLoginOption();

            return Page();
        }

        // A second factor is any of: TOTP enabled, or a registered passkey (ADR "WebAuthn passkeys").
        var hasTotp = user.MfaEnabledAt is not null && user.TotpSecret is not null;
        var passkeys = await _dbContext.WebAuthnCredentials
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(c => c.UserId == user.Id)
            .Select(c => c.CredentialId)
            .ToListAsync();

        // No second factor: if the tenant requires MFA, force inline enrolment before signing in; else straight in.
        if (!hasTotp && passkeys.Count == 0)
        {
            var requireMfa = await _dbContext.Tenants
                .Where(t => t.Id == user.TenantId)
                .Select(t => t.RequireMfa)
                .FirstOrDefaultAsync();
            if (requireMfa)
            {
                StartEnroll(user);
                return Page();
            }

            return await SignInAndRedirectAsync(user);
        }

        // Has a second factor → issue the signed ticket and render the challenge (TOTP field and/or passkey).
        MfaTicket = _ticketProtector.Protect($"{user.Id}|{ReturnUrl}", MfaTicketLifetime);
        PrepareChallenge(user, hasTotp, passkeys);

        return Page();
    }

    // Renders the second-factor challenge: a TOTP field when the user has TOTP, and a passkey button (with the
    // assertion options + a signed passkey token) when they have registered passkeys.
    private void PrepareChallenge(User user, bool hasTotp, IReadOnlyList<byte[]> passkeys)
    {
        ShowMfa = true;
        ShowTotp = hasTotp;
        if (passkeys.Count > 0)
        {
            var options = _fido2.GetAssertionOptions(new Fido2NetLib.GetAssertionOptionsParams
            {
                AllowedCredentials = passkeys.Select(id => new Fido2NetLib.Objects.PublicKeyCredentialDescriptor(id)).ToList(),
                UserVerification = Fido2NetLib.Objects.UserVerificationRequirement.Preferred,
            });
            ShowPasskey = true;
            PasskeyOptionsJson = options.ToJson();
            PasskeyToken = _passkeyProtector.Protect($"{user.Id}|{ReturnUrl}|{options.ToJson()}", MfaTicketLifetime);
        }
    }

    public async Task<IActionResult> OnPostVerifyAsync()
    {
        if (string.IsNullOrWhiteSpace(MfaTicket) || !TryReadTicket(MfaTicket, out var userId))
        {
            // Ticket missing/expired/tampered → back to the password step.
            Error = SimplArchive.Localization.Strings.Get("LoginErrSessionExpiredSignIn");

            return Page();
        }

        var user = await _dbContext.Users
            .IgnoreQueryFilters(["TenantFilter"])
            .SingleOrDefaultAsync(u => u.Id == userId);

        if (user is null || !user.IsActive || user.MfaEnabledAt is null || user.TotpSecret is null)
        {
            // A valid MFA ticket that no longer resolves to an MFA-eligible user: deactivated mid-login, MFA
            // turned off, or a tampered ticket. Rare and worth seeing — the caller is told only "invalid
            // credentials", so without this the event exists nowhere.
            _logger.LogWarning(
                "Second-factor ticket rejected for user {UserId}: no active user with MFA configured", userId);

            Error = SimplArchive.Localization.Strings.Get("LoginErrInvalidCreds");

            return Page();
        }

        if (string.IsNullOrWhiteSpace(Code) || !await VerifySecondFactorAsync(user, Code))
        {
            // A wrong SECOND factor is a failed authentication with exactly the SIEM value of a wrong
            // password (logged above) — and it is the more interesting half: reaching here means the password
            // already succeeded, so repeated failures are somebody brute-forcing six digits with a credential
            // they hold. Until this line, that produced no signal at all (#595, ADR 0626).
            //
            // The code itself is never logged. It is a credential for its window, and a log is the wrong place
            // for it even after it expires.
            _logger.LogWarning("Failed second factor for user {UserId}: incorrect or missing code", user.Id);

            Error = SimplArchive.Localization.Strings.Get("LoginErrInvalidCode");
            // Re-issue a fresh ticket + re-render the challenge (incl. the passkey option) for a retry.
            MfaTicket = _ticketProtector.Protect($"{user.Id}|{ReturnUrl}", MfaTicketLifetime);
            await RerenderChallengeAsync(user);

            return Page();
        }

        return await SignInAndRedirectAsync(user);
    }

    // Verifies a passkey assertion at the login challenge (ADR "WebAuthn passkeys as a second factor").
    public async Task<IActionResult> OnPostPasskeyAsync()
    {
        if (string.IsNullOrWhiteSpace(MfaTicket) || !TryReadTicket(MfaTicket, out var userId))
        {
            Error = SimplArchive.Localization.Strings.Get("LoginErrSessionExpiredSignIn");
            return Page();
        }

        var user = await _dbContext.Users.IgnoreQueryFilters(["TenantFilter"]).SingleOrDefaultAsync(u => u.Id == userId);
        if (user is null || !user.IsActive)
        {
            Error = SimplArchive.Localization.Strings.Get("LoginErrInvalidCreds");
            return Page();
        }

        if (string.IsNullOrWhiteSpace(PasskeyToken) || string.IsNullOrWhiteSpace(PasskeyResponse) || !await VerifyPasskeyAsync(user))
        {
            Error = SimplArchive.Localization.Strings.Get("LoginErrPasskeyMfaFailed");
            MfaTicket = _ticketProtector.Protect($"{user.Id}|{ReturnUrl}", MfaTicketLifetime);
            await RerenderChallengeAsync(user);
            return Page();
        }

        return await SignInAndRedirectAsync(user);
    }

    // Verifies a passwordless (discoverable) passkey assertion (ADR "Passwordless passkey login") — no
    // email/password was entered; the assertion's user handle identifies the user. Enforces the per-tenant
    // AllowPasskeyLogin policy; a passkey with user verification satisfies require-MFA, so it signs straight in.
    public async Task<IActionResult> OnPostPasskeyLoginAsync()
    {
        if (string.IsNullOrWhiteSpace(PasskeyLoginToken) || string.IsNullOrWhiteSpace(PasskeyLoginResponse))
        {
            Error = SimplArchive.Localization.Strings.Get("LoginErrPasskeyFailed");
            PreparePasskeyLoginOption();
            return Page();
        }

        Fido2NetLib.AssertionOptions options;
        Fido2NetLib.AuthenticatorAssertionRawResponse response;
        try
        {
            options = Fido2NetLib.AssertionOptions.FromJson(_passkeyLoginlessProtector.Unprotect(PasskeyLoginToken));
            response = System.Text.Json.JsonSerializer.Deserialize<Fido2NetLib.AuthenticatorAssertionRawResponse>(PasskeyLoginResponse)!;
        }
        catch (Exception)
        {
            Error = SimplArchive.Localization.Strings.Get("LoginErrSessionExpiredTry");
            PreparePasskeyLoginOption();
            return Page();
        }

        // Resolve the credential by its id (across all tenants — no tenant is known at login), then the owner.
        var credential = await _dbContext.WebAuthnCredentials
            .IgnoreQueryFilters(["TenantFilter"])
            .SingleOrDefaultAsync(c => c.CredentialId == response.RawId);
        var user = credential is null ? null : await _dbContext.Users
            .IgnoreQueryFilters(["TenantFilter"])
            .SingleOrDefaultAsync(u => u.Id == credential.UserId);

        if (credential is null || user is null || !user.IsActive)
        {
            Error = SimplArchive.Localization.Strings.Get("LoginErrPasskeyFailed");
            PreparePasskeyLoginOption();
            return Page();
        }

        // Enforce the per-tenant policy (and that the tenant is active).
        var tenant = await _dbContext.Tenants
            .Where(t => t.Id == user.TenantId)
            .Select(t => new { t.Status, t.AllowPasskeyLogin })
            .FirstOrDefaultAsync();
        if (tenant is null || tenant.Status != TenantStatus.Active || !tenant.AllowPasskeyLogin)
        {
            Error = SimplArchive.Localization.Strings.Get("LoginErrPasskeyNotEnabled");
            PreparePasskeyLoginOption();
            return Page();
        }

        try
        {
            var result = await _fido2.MakeAssertionAsync(new Fido2NetLib.MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = options,
                StoredPublicKey = credential.PublicKey,
                StoredSignatureCounter = (uint)credential.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, ct) =>
                    Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(user.Id.ToByteArray())),
            });

            credential.SignCount = result.SignCount;
            credential.LastUsedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync();
        }
        catch (Fido2NetLib.Fido2VerificationException)
        {
            Error = SimplArchive.Localization.Strings.Get("LoginErrPasskeyFailed");
            PreparePasskeyLoginOption();
            return Page();
        }

        return await SignInAndRedirectAsync(user);
    }

    private async Task RerenderChallengeAsync(User user)
    {
        var hasTotp = user.MfaEnabledAt is not null && user.TotpSecret is not null;
        var passkeys = await _dbContext.WebAuthnCredentials
            .IgnoreQueryFilters(["TenantFilter"]).Where(c => c.UserId == user.Id).Select(c => c.CredentialId).ToListAsync();
        PrepareChallenge(user, hasTotp, passkeys);
    }

    private async Task<bool> VerifyPasskeyAsync(User user)
    {
        Fido2NetLib.AssertionOptions options;
        try
        {
            // The passkey token = userId|returnUrl|optionsJson (optionsJson last, so split into at most 3).
            var parts = _passkeyProtector.Unprotect(PasskeyToken!).Split('|', 3);
            if (parts.Length < 3 || !Guid.TryParse(parts[0], out var ticketUserId) || ticketUserId != user.Id)
            {
                return false;
            }

            options = Fido2NetLib.AssertionOptions.FromJson(parts[2]);
        }
        catch (Exception)
        {
            return false;
        }

        Fido2NetLib.AuthenticatorAssertionRawResponse response;
        try
        {
            response = System.Text.Json.JsonSerializer.Deserialize<Fido2NetLib.AuthenticatorAssertionRawResponse>(PasskeyResponse!)!;
        }
        catch (Exception)
        {
            return false;
        }

        var credentialId = response.RawId;
        var credential = await _dbContext.WebAuthnCredentials
            .IgnoreQueryFilters(["TenantFilter"])
            .SingleOrDefaultAsync(c => c.UserId == user.Id && c.CredentialId == credentialId);
        if (credential is null)
        {
            return false;
        }

        try
        {
            var result = await _fido2.MakeAssertionAsync(new Fido2NetLib.MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = options,
                StoredPublicKey = credential.PublicKey,
                StoredSignatureCounter = (uint)credential.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, ct) =>
                    Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(user.Id.ToByteArray())),
            });

            credential.SignCount = result.SignCount;
            credential.LastUsedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync();
            return true;
        }
        catch (Fido2NetLib.Fido2VerificationException)
        {
            return false;
        }
    }

    // Begins inline enrolment: generates a pending secret, renders the QR + code step, and carries the secret
    // in a short-lived signed ticket (nothing is written to the DB until the code is confirmed).
    private void StartEnroll(User user)
    {
        var secret = _mfa.GenerateSecret();
        EnrollTicket = _enrollProtector.Protect($"{user.Id}|{ReturnUrl}|{secret}", MfaTicketLifetime);
        RenderEnrollStep(secret, user.Email);
    }

    private void RenderEnrollStep(string secret, string email)
    {
        EnrollSecret = secret;
        EnrollQrDataUrl = $"data:image/png;base64,{Convert.ToBase64String(_mfa.GenerateQrPng(_mfa.BuildOtpauthUri(secret, email)))}";
        ShowEnroll = true;
    }

    // Confirms inline enrolment: verifies the code against the ticket's pending secret, enables MFA (secret
    // stored encrypted), issues the one-time recovery codes, and renders the recovery-codes step.
    public async Task<IActionResult> OnPostEnrollAsync()
    {
        if (string.IsNullOrWhiteSpace(EnrollTicket) || !TryReadEnrollTicket(EnrollTicket, out var userId, out var secret))
        {
            Error = SimplArchive.Localization.Strings.Get("LoginErrSessionExpiredSignIn");
            return Page();
        }

        var user = await _dbContext.Users.IgnoreQueryFilters(["TenantFilter"]).SingleOrDefaultAsync(u => u.Id == userId);
        if (user is null || !user.IsActive)
        {
            Error = SimplArchive.Localization.Strings.Get("LoginErrInvalidCreds");
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Code) || !_mfa.VerifyTotp(secret, Code))
        {
            Error = SimplArchive.Localization.Strings.Get("LoginErrInvalidCode");
            RenderEnrollStep(secret, user.Email); // keep the same ticket/secret so the user can retry
            return Page();
        }

        user.TotpSecret = await _transit.EncryptAsync(secret); // encrypted at rest (OpenBao transit)
        user.MfaEnabledAt = DateTimeOffset.UtcNow;

        var existing = await _dbContext.UserRecoveryCodes.IgnoreQueryFilters(["TenantFilter"]).Where(c => c.UserId == user.Id).ToListAsync();
        _dbContext.UserRecoveryCodes.RemoveRange(existing);

        var codes = _mfa.GenerateRecoveryCodes();
        foreach (var (_, hash) in codes)
        {
            _dbContext.UserRecoveryCodes.Add(new UserRecoveryCode
            {
                Id = Guid.NewGuid(),
                TenantId = user.TenantId,
                UserId = user.Id,
                CodeHash = hash,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _dbContext.SaveChangesAsync();

        RecoveryCodes = codes.Select(c => c.Plaintext).ToList();
        ContinueTicket = _enrollProtector.Protect($"{user.Id}|{ReturnUrl}", MfaTicketLifetime);
        ShowRecoveryCodes = true;
        return Page();
    }

    // Completes sign-in after the user has saved their recovery codes.
    public async Task<IActionResult> OnPostContinueAsync()
    {
        if (string.IsNullOrWhiteSpace(ContinueTicket) || !TryReadEnrollTicket(ContinueTicket, out var userId, out _))
        {
            Error = SimplArchive.Localization.Strings.Get("LoginErrSessionExpiredSignIn");
            return Page();
        }

        var user = await _dbContext.Users.IgnoreQueryFilters(["TenantFilter"]).SingleOrDefaultAsync(u => u.Id == userId);
        if (user is null || !user.IsActive || user.MfaEnabledAt is null)
        {
            Error = SimplArchive.Localization.Strings.Get("LoginErrInvalidCreds");
            return Page();
        }

        return await SignInAndRedirectAsync(user);
    }

    // A valid TOTP, or a matching unused recovery code (consumed on success).
    private async Task<bool> VerifySecondFactorAsync(User user, string code)
    {
        if (_mfa.VerifyTotp(await _transit.DecryptAsync(user.TotpSecret!), code))
        {
            return true;
        }

        var hash = _mfa.HashRecoveryCode(code);
        var recovery = await _dbContext.UserRecoveryCodes
            .IgnoreQueryFilters(["TenantFilter"])
            .FirstOrDefaultAsync(c => c.UserId == user.Id && c.UsedAt == null && c.CodeHash == hash);

        if (recovery is null)
        {
            return false;
        }

        recovery.UsedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync();

        return true;
    }

    // Reads an enrolment/continue ticket (userId|returnUrl[|secret]) protected by the enrol protector. Sets
    // ReturnUrl so it survives the multi-step flow; secret is "" for a 2-part continue ticket.
    private bool TryReadEnrollTicket(string ticket, out Guid userId, out string secret)
    {
        userId = Guid.Empty;
        secret = "";
        try
        {
            var parts = _enrollProtector.Unprotect(ticket).Split('|');
            if (parts.Length < 2 || !Guid.TryParse(parts[0], out userId))
            {
                return false;
            }

            ReturnUrl = string.IsNullOrEmpty(parts[1]) ? ReturnUrl : parts[1];
            secret = parts.Length >= 3 ? parts[2] : "";
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private bool TryReadTicket(string ticket, out Guid userId)
    {
        userId = Guid.Empty;
        try
        {
            var payload = _ticketProtector.Unprotect(ticket); // throws if expired/tampered
            var separator = payload.IndexOf('|');
            var idPart = separator < 0 ? payload : payload[..separator];
            var returnPart = separator < 0 ? null : payload[(separator + 1)..];

            if (!Guid.TryParse(idPart, out userId))
            {
                return false;
            }

            // The ticket carries the original returnUrl so it survives the second step.
            ReturnUrl = string.IsNullOrEmpty(returnPart) ? ReturnUrl : returnPart;

            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private async Task<IActionResult> SignInAndRedirectAsync(User user)
    {
        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, user.Id.ToString()));

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        // The current-principal accessors aren't populated on this anonymous POST, so the actor is supplied
        // explicitly. See ADR "Audit trail (first slice)".
        await _audit.RecordForActorAsync(
            AuditActorType.User,
            user.Id,
            user.DisplayName,
            user.TenantId,
            AuditActions.LoggedIn);

        _logger.LogInformation("User {UserId} signed in to tenant {TenantId}", user.Id, user.TenantId);

        return LocalRedirect(string.IsNullOrEmpty(ReturnUrl) ? "/" : ReturnUrl);
    }
}
