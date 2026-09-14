using System.Security.Cryptography;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Manages the caller's app-specific WebDAV password (ADR "WebDAV gateway") — a separate credential from the
/// login password, so it isn't typed into an OS keychain (and MFA users can still mount). Generate returns the
/// plaintext once; only its hash is stored (<c>User.WebDavPasswordHash</c>). User-only — a ServiceAccount has
/// no WebDAV mount.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/me/webdav-password")]
[Authorize]
public class WebDavAccessController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    // Through the user's verb contract (ADR 0795): every write here is a column on the USER row, and a
    // WebDAV credential is exactly the kind two admin sessions can clobber — one issues while the other
    // revokes, and last-write-wins decides whether a long-lived password that bypasses interactive login
    // still exists. Tolerant of an absent If-Match, so no caller breaks.
    private readonly Concurrency.UserVerbs _users;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IConfiguration _configuration;
    private readonly PasswordHasher<User> _passwordHasher = new();
    private readonly IAuditRecorder _audit;

    public WebDavAccessController(SimplArchiveDbContext dbContext, ICurrentUserAccessor currentUserAccessor, IConfiguration configuration, IAuditRecorder audit,
        Concurrency.UserVerbs users)
    {
        _dbContext = dbContext;
        _users = users;
        _currentUserAccessor = currentUserAccessor;
        _configuration = configuration;
        _audit = audit;
    }

    public class WebDavStatusResource : HypermediaResource
    {
        public bool Enabled { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    public class WebDavPasswordResource : WebDavStatusResource
    {
        // The generated password — returned ONCE at generation; only its hash is stored.
        public string Password { get; set; } = string.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        return Ok(new WebDavStatusResource { Enabled = user.WebDavPasswordHash is not null, Username = user.Email, Url = MountUrl() });
    }

    [HttpHead]
    public async Task<IActionResult> Head(CancellationToken cancellationToken) =>
        await LoadUserAsync(cancellationToken) is null ? Forbid() : NoContent();

    // Generate (or regenerate) the WebDAV password — returns the plaintext once; only the hash is stored.
    [HttpPost]
    public async Task<IActionResult> Generate(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        // A URL/Basic-auth-safe password (hex — no +/=/: characters to escape).
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        user.WebDavPasswordHash = _passwordHasher.HashPassword(user, password);
        await _users.MutateAsync(Request, user, apply: () => Task.CompletedTask, cancellationToken: cancellationToken);

        // Issuing one hands out a long-lived password that bypasses the interactive login and every MFA
        // policy attached to it (#1092) — precisely what a SIEM watches for, and until now unrecorded. The
        // password itself is never logged: the event says that one was issued, to whom, and when.
        await _audit.RecordAsync(AuditActions.WebDavPasswordIssued, "User", user.Id, user.Email,
            cancellationToken: cancellationToken);

        return Ok(new WebDavPasswordResource { Enabled = true, Username = user.Email, Url = MountUrl(), Password = password });
    }

    // Revoke WebDAV access — clears the stored hash.
    [HttpDelete]
    public async Task<IActionResult> Revoke(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        user.WebDavPasswordHash = null;
        await _users.MutateAsync(Request, user, apply: () => Task.CompletedTask, cancellationToken: cancellationToken);

        // The revocation matters as much as the issue: it is what an incident response does, and a trail
        // showing the credential issued but never withdrawn tells the wrong story.
        await _audit.RecordAsync(AuditActions.WebDavPasswordIssued, "User", user.Id, user.Email,
            "Revoked", cancellationToken: cancellationToken);
        return NoContent();
    }

    private async Task<User?> LoadUserAsync(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return null; // a ServiceAccount / platform admin has no WebDAV mount
        }

        return await _dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
    }

    private string MountUrl()
    {
        // The single "SimplArchive" resource (ADR 0509): mounting it names the OS volume "SimplArchive" and lists
        // the whole tree (Personal + the shared repositories the user can see). The /webdav alias is retired
        // (#794), so a mount saved against it must be re-created against this URL.
        var baseUrl = (_configuration["App:BaseUrl"] ?? "").TrimEnd('/');
        return $"{baseUrl}/SimplArchive";
    }
}
