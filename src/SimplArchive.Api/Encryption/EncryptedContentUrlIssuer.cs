using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.Encryption;

/// <summary>
/// Issues the core-served substitute for a presigned URL on encrypted objects (ADR 0818): a time-limited,
/// self-authorizing token protecting the object key + serve options, handed out as a RELATIVE URL so both
/// clients resolve it against their own base — same trust shape as a presigned link (whoever holds it may
/// fetch, until it expires), different door (<see cref="Controllers.EncryptedContentController"/>).
/// </summary>
public sealed class EncryptedContentUrlIssuer(IDataProtectionProvider dataProtection) : IEncryptedContentUrlIssuer
{
    internal const string Purpose = "encrypted-content-url";

    internal sealed record Token(string ObjectKey, string? FileName, string? ContentType, bool Inline);

    public Uri Issue(string objectKey, TimeSpan expiry, string? fileName = null, string? contentType = null, bool inline = false)
    {
        var protector = dataProtection.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        var token = protector.Protect(
            JsonSerializer.Serialize(new Token(objectKey, fileName, contentType, inline)), expiry);
        return new Uri($"/api/encrypted-content?t={Uri.EscapeDataString(token)}", UriKind.Relative);
    }
}
