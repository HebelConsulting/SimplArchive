using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Encryption;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Serves an encrypted-at-rest object as plaintext against a time-limited token (ADR 0818) — the
/// core-served door that replaces a presigned S3 link where the bucket holds ciphertext. Anonymous by
/// design, exactly like the presigned URL it substitutes: the TOKEN is the authorization (issued only to
/// callers who already passed the resource's own gate), it expires, and it names one object. The bytes
/// come through the at-rest decorator, which is what does the decrypting.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/encrypted-content")]
[AllowAnonymous]
public class EncryptedContentController : ControllerBase
{
    private readonly IObjectStorageClient _storage;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly ILogger<EncryptedContentController> _logger;

    public EncryptedContentController(
        IObjectStorageClient storage,
        IDataProtectionProvider dataProtection,
        ILogger<EncryptedContentController> logger)
    {
        _storage = storage;
        _dataProtection = dataProtection;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery(Name = "t")] string token, CancellationToken cancellationToken)
    {
        if (Unprotect(token) is not { } payload)
        {
            return NotFound(); // expired or forged — indistinguishable on purpose, like a dead presigned link
        }

        var content = await _storage.GetObjectForClientAsync(payload.ObjectKey, "the content link", cancellationToken);
        if (payload.Inline)
        {
            Response.Headers.ContentDisposition = payload.FileName is { } inlineName
                ? $"inline; filename=\"{Uri.EscapeDataString(inlineName)}\""
                : "inline";
            return File(content, payload.ContentType ?? "application/octet-stream");
        }

        return File(content, payload.ContentType ?? "application/octet-stream",
            payload.FileName ?? payload.ObjectKey.Split('/')[^1]);
    }

    [HttpHead]
    public async Task<IActionResult> Head([FromQuery(Name = "t")] string token, CancellationToken cancellationToken)
    {
        if (Unprotect(token) is not { } payload)
        {
            return NotFound();
        }

        var info = await _storage.GetObjectInfoAsync(payload.ObjectKey, cancellationToken);
        Response.ContentLength = info.Size;
        Response.ContentType = payload.ContentType ?? "application/octet-stream";
        return NoContent();
    }

    private EncryptedContentUrlIssuer.Token? Unprotect(string token)
    {
        try
        {
            var protector = _dataProtection.CreateProtector(EncryptedContentUrlIssuer.Purpose).ToTimeLimitedDataProtector();
            return JsonSerializer.Deserialize<EncryptedContentUrlIssuer.Token>(protector.Unprotect(token));
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException)
        {
            _logger.LogDebug("Encrypted-content token refused: {Reason}.", exception.Message);
            return null;
        }
    }
}
