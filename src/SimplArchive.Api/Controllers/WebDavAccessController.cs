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
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IConfiguration _configuration;
    private readonly PasswordHasher<User> _passwordHasher = new();

    public WebDavAccessController(SimplArchiveDbContext dbContext, ICurrentUserAccessor currentUserAccessor, IConfiguration configuration)
    {
        _dbContext = dbContext;
        _currentUserAccessor = currentUserAccessor;
        _configuration = configuration;
    }

    public class WebDavStatusResource : HypermediaResource
    {
        public bool Enabled { get; set; }
        public string Username { get; set; } = "";
        public string Url { get; set; } = "";
    }

    public class WebDavPasswordResource : WebDavStatusResource
    {
        // The generated password — returned ONCE at generation; only its hash is stored.
        public string Password { get; set; } = "";
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
        await _dbContext.SaveChangesAsync(cancellationToken);

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
        await _dbContext.SaveChangesAsync(cancellationToken);
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
        var baseUrl = (_configuration["App:BaseUrl"] ?? "").TrimEnd('/');
        return $"{baseUrl}/webdav";
    }
}
