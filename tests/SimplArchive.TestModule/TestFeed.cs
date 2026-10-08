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
/// The presigned-upload fixture (ABI 1.12, core ADR 0910), under the header-credential prefix <c>apps</c>: begin, then
/// open (proving the read is seekable) and file. It reports what it read, so a test can compare it with what it sent.
/// </summary>
[ApiController]
[Authorize]
[Route("apps/test-upload")]
public sealed class TestUploadController(IModuleArchiveFacade archive) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Begin([FromQuery] long maxBytes)
    {
        var upload = await archive.BeginUploadAsync(maxBytes);
        return Ok(new { upload.UploadId, url = upload.Url.ToString(), upload.ExpiresAt });
    }

    [HttpPost("{uploadId}")]
    public async Task<IActionResult> File(string uploadId, [FromQuery] Guid folder, [FromQuery] string name)
    {
        string text;
        bool seekable;
        await using (var content = await archive.OpenUploadAsync(uploadId))
        {
            seekable = content.CanSeek;
            content.Seek(0, SeekOrigin.End);       // what an APK reader does first: the end record
            content.Seek(0, SeekOrigin.Begin);
            text = await new StreamReader(content).ReadToEndAsync();
        }

        var id = await archive.FileUploadAsync(uploadId, folder, TestModule.EntryMaskId, name, ".txt");
        return Ok(new { id, text, seekable });
    }

    [HttpDelete("{uploadId}")]
    public async Task<IActionResult> Discard(string uploadId)
    {
        await archive.DiscardUploadAsync(uploadId);
        return NoContent();
    }
}

/// <summary>
/// The path-credential fixture (ABI 1.11, core ADR 0909): an app-repository client sends no authentication, so the
/// credential is the path segment after <c>fdroid</c>. The core cut it out before routing, so this route is the plain
/// path, and the response echoes the path it saw, so a test can prove the secret never reached the module's routing.
/// </summary>
[ApiController]
[Authorize]
[Route("fdroid/test-repo")]
public sealed class TestRepoController(IModuleCredentialContext credential) : ControllerBase
{
    [HttpGet("{**rest}")]
    public IActionResult Get(string? rest) => Ok(new { subject = credential.Identity?.Subject, path = Request.Path.Value, rest });
}

/// <summary>
/// Accepts one secret, and reads a module setting through the FACADE while doing so: the facade answers settings
/// only when the request acts as the module, so the subject it returns proves the core set tenant and principal
/// BEFORE calling here.
/// </summary>
public sealed class TestCredentialAuthenticator(IModuleArchiveFacade archive) : IModuleCredentialAuthenticator
{
    public const string Secret = "open-sesame";

    public IReadOnlyList<string> RoutePrefixes => ["nuget/test-feed", "fdroid/test-repo", "apps/test-upload"];

    /// <summary>The app-repository fixture: on <c>fdroid</c> the credential is the first segment (ABI 1.11, ADR 0909).</summary>
    public IReadOnlyList<string> PathCredentialPrefixes => ["fdroid"];

    public IReadOnlyList<string> ApiKeyHeaders => ["X-Test-ApiKey"];

    public ModuleChallenge Challenge => new("Basic", "SimplArchive test feed");

    public async Task<ModuleCredentialIdentity?> AuthenticateAsync(string secret, CancellationToken cancellationToken) =>
        secret == Secret
            ? new ModuleCredentialIdentity($"customer-1@{await archive.GetSettingAsync("endpoint", cancellationToken) ?? "unset"}", "Test customer")
            : null;
}
