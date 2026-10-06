using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.ModuleAbi;

namespace SimplArchive.TestModule;

/// <summary>
/// The protocol-route fixture (ABI 1.7, core ADR 0898): a controller under the claimed ROOT prefix <c>nuget</c>,
/// reached by a protocol credential rather than a login. It reports what the request authenticated as, so a test
/// can see the tenant, the module's own principal and the module's subject arrive together.
/// </summary>
[ApiController]
[Authorize]
[Route("nuget/test-feed")]
public sealed class TestFeedController(IModuleCredentialContext credential, IModuleCallerContext caller) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        subject = credential.Identity?.Subject,
        tenantId = caller.TenantId,
        serviceAccountId = caller.ServiceAccountId,
        userId = caller.UserId,
    });

    [HttpHead]
    public IActionResult Head() => Ok();
}

/// <summary>
/// Accepts one secret, and reads a module setting through the FACADE while doing so: the facade answers settings
/// only when the request acts as the module, so the subject it returns proves the core set tenant and principal
/// BEFORE calling here.
/// </summary>
public sealed class TestCredentialAuthenticator(IModuleArchiveFacade archive) : IModuleCredentialAuthenticator
{
    public const string Secret = "open-sesame";

    public IReadOnlyList<string> RoutePrefixes => ["nuget/test-feed"];

    public IReadOnlyList<string> ApiKeyHeaders => ["X-Test-ApiKey"];

    public ModuleChallenge Challenge => new("Basic", "SimplArchive test feed");

    public async Task<ModuleCredentialIdentity?> AuthenticateAsync(string secret, CancellationToken cancellationToken) =>
        secret == Secret
            ? new ModuleCredentialIdentity($"customer-1@{await archive.GetSettingAsync("endpoint", cancellationToken) ?? "unset"}", "Test customer")
            : null;
}
