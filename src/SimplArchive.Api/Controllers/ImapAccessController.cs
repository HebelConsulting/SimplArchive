using System.Security.Cryptography;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Imap;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Manages the caller's app-specific IMAP password + view toggle (ADR "IMAP endpoint (read-only, first
/// slice)", #562) — the WebDAV credential's exact pattern: a separate generated password per protocol surface,
/// plaintext returned once, only the hash stored. User-only — no ServiceAccount has a mailbox.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/me/imap-access")]
[Authorize]
public class ImapAccessController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IOptions<ImapOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly PersonalMailboxProvisioner _mailbox;
    private readonly PasswordHasher<User> _passwordHasher = new();

    public ImapAccessController(
        SimplArchiveDbContext dbContext,
        ICurrentUserAccessor currentUserAccessor,
        ICurrentTenantAccessor currentTenantAccessor,
        IOptions<ImapOptions> options,
        IConfiguration configuration,
        PersonalMailboxProvisioner mailbox)
    {
        _dbContext = dbContext;
        _currentUserAccessor = currentUserAccessor;
        _currentTenantAccessor = currentTenantAccessor;
        _options = options;
        _configuration = configuration;
        _mailbox = mailbox;
    }

    public class ImapStatusResource : HypermediaResource
    {
        /// <summary>The endpoint exists at all (Imap:Enabled) — off means the dialog says so instead of showing dead settings.</summary>
        public bool Available { get; set; }

        /// <summary>The caller holds a generated IMAP password.</summary>
        public bool Enabled { get; set; }

        public string Username { get; set; } = "";

        public string Host { get; set; } = "";

        public int? Port { get; set; }

        public int? TlsPort { get; set; }

        /// <summary>The user's own view choice (#562): every visible document vs emails only.</summary>
        public bool ShowAllDocuments { get; set; }
    }

    public class ImapPasswordResource : ImapStatusResource
    {
        // The generated password — returned ONCE at generation; only its hash is stored.
        public string Password { get; set; } = "";
    }

    public class ImapSettingsRequest
    {
        public bool ShowAllDocuments { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        await LoadUserAsync(cancellationToken) is not { } user ? Forbid() : Ok(Status<ImapStatusResource>(user));

    [HttpHead]
    public async Task<IActionResult> Head(CancellationToken cancellationToken) =>
        await LoadUserAsync(cancellationToken) is null ? Forbid() : NoContent();

    // Generate (or regenerate) the IMAP password — returns the plaintext once; only the hash is stored.
    [HttpPost]
    public async Task<IActionResult> Generate(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        user.ImapPasswordHash = _passwordHasher.HashPassword(user, password);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // The SECOND trigger for the mailbox (#562). The first is a delivered message, and on its own it leaves
        // a user who has just configured their mail client with nothing to subscribe to — from which they
        // conclude the feature is broken, when the archive is only waiting for mail that may be days away.
        // Generating a credential is an unambiguous statement of intent to use the mailbox, so it counts as
        // demand in its own right; whichever trigger fires first creates the node and the other finds it.
        if (_currentTenantAccessor.TenantId is { } tenantId)
        {
            await _mailbox.EnsureMailboxAsync(tenantId, user.Id, cancellationToken);
        }

        var resource = Status<ImapPasswordResource>(user);
        resource.Password = password;
        return Ok(resource);
    }

    // Revoke IMAP access — clears the stored hash.
    [HttpDelete]
    public async Task<IActionResult> Revoke(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        user.ImapPasswordHash = null;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    // The self-service view toggle (#562) — deliberately its own PUT so the dialog's switch is one idempotent
    // full-value write, the ETag-less shape every boolean me-setting uses.
    [HttpPut("settings")]
    public async Task<IActionResult> PutSettings([FromBody] ImapSettingsRequest request, CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        user.ImapShowAllDocuments = request.ShowAllDocuments;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private T Status<T>(User user) where T : ImapStatusResource, new()
    {
        var options = _options.Value;
        var host = options.PublicHost
            ?? new Uri(_configuration["App:BaseUrl"] ?? "http://localhost").Host;
        return new T
        {
            Available = options.Enabled,
            Enabled = user.ImapPasswordHash is not null,
            Username = user.Email,
            Host = host,
            // The PUBLISHED port, not the bound one — a user can only act on what is reachable from outside,
            // and the container-internal port must never reach a screen (#682).
            Port = options.Enabled ? options.AdvertisedPort : null,
            TlsPort = options.Enabled ? options.AdvertisedTlsPort : null,
            ShowAllDocuments = user.ImapShowAllDocuments,
            Links =
            [
                // ONE rel for this address; the method says which action (ADR 0719). GET reads the status,
                // POST issues a password, DELETE revokes it — and `generate`/`revoke` said nothing the method
                // did not already carry. What gates the two buttons is `available` and `enabled` below, which
                // the dialog already used: the rels were never the signal, which is why removing them costs
                // nothing. `settings` stays a rel of its own — it is a different address.
                new Link("self", "/api/me/imap-access", "GET"),
                new Link("settings", "/api/me/imap-access/settings", "PUT"),
            ],
        };
    }

    private async Task<User?> LoadUserAsync(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return null; // a ServiceAccount / platform admin has no mailbox
        }

        return await _dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
    }
}
