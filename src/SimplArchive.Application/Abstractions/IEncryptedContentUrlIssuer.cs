namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Issues the core-served substitute for a presigned URL when the object is encrypted at rest (ADR 0818):
/// a presigned S3 link would hand the browser CIPHERTEXT, so the at-rest decorator swaps in a short-lived,
/// self-authorizing core URL whose handler decrypts on the way out — same semantics as a presigned link
/// (time-limited, bearer-free), different door. Implemented in the Api (it owns routes and the token
/// protector); Infrastructure consumes it through this seam.
/// </summary>
public interface IEncryptedContentUrlIssuer
{
    Uri Issue(string objectKey, TimeSpan expiry, string? fileName = null, string? contentType = null, bool inline = false);
}
