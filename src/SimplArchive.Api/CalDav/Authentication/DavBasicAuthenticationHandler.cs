// PORTED from the sister project SimplCalCon (Apache-2.0, same licence) — see ADR 0621. ADAPTED: the
// credential is SimplArchive's SHARED DAV password (User.WebDavPasswordHash — one secret covering WebDAV,
// CalDAV and CardDAV, the epic's decision), and the authenticated principal additionally carries the tenant,
// because every read here is scoped by the tenant query filter.
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.CalDav.Authentication;

public sealed class DavBasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string BasicPrefix = "Basic ";

    /// <summary>The claim carrying the authenticated user's tenant — read back by the DAV controllers.</summary>
    public const string TenantClaim = "simplarchive:tenant";

    private readonly SimplArchiveDbContext _dbContext;
    private readonly PasswordHasher<User> _passwordHasher = new();

    public DavBasicAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, SimplArchiveDbContext dbContext)
        : base(options, logger, encoder)
    {
        _dbContext = dbContext;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
        {
            return AuthenticateResult.NoResult();
        }

        var value = header.ToString();
        if (!value.StartsWith(BasicPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value[BasicPrefix.Length..].Trim()));
        }
        catch (FormatException)
        {
            return AuthenticateResult.Fail("Malformed Basic credentials.");
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0)
        {
            return AuthenticateResult.Fail("Malformed Basic credentials.");
        }

        var normalized = decoded[..separator].ToUpperInvariant();
        var password = decoded[(separator + 1)..];

        // No tenant is known yet — the standing pre-tenant-lookup rule (ADR 0150 / TokenController).
        var user = await _dbContext.Users.IgnoreQueryFilters(["TenantFilter"])
            .SingleOrDefaultAsync(u => u.NormalizedEmail == normalized && u.IsActive);
        if (user?.WebDavPasswordHash is null
            || _passwordHasher.VerifyHashedPassword(user, user.WebDavPasswordHash, password) == PasswordVerificationResult.Failed)
        {
            return AuthenticateResult.Fail("Invalid DAV credentials.");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(TenantClaim, user.TenantId.ToString()),
        ], DavAuthenticationDefaults.Scheme);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), DavAuthenticationDefaults.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = $"Basic realm=\"{DavAuthenticationDefaults.Realm}\"";
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
