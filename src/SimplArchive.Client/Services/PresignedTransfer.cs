namespace SimplArchive.Client.Services;

/// <summary>
/// The client used for a presigned object-storage transfer.
/// </summary>
/// <remarks>
/// Deliberately anonymous and separate from the authenticated <c>HttpClient</c>: a presigned URL carries its
/// own authorisation in the query string, and attaching a bearer token to a cross-origin S3 PUT would fail
/// CORS preflight. Shared rather than one instance per component — an HttpClient is meant to be long-lived,
/// and each new one is a fresh connection pool.
/// </remarks>
public static class PresignedTransfer
{
    public static HttpClient Anonymous { get; } = new();
}
