namespace SimplArchive.Api.Errors;

// Lets application code throw with a specific, stable errorCode rather than a generic unhandled
// exception — the global exception handler (see ADR "Hypermedia envelope and Problem Details errors
// (foundation slice)") translates this into an RFC 7807 Problem Details response carrying ErrorCode as
// the `errorCode` extension member and StatusCode as the HTTP status.
public class ApiException : Exception
{
    public string ErrorCode { get; }

    public int StatusCode { get; }

    /// <summary>
    /// Machine-readable facts riding the Problem Details response as extension members (#703's first use:
    /// <c>claimedBy</c> names the mailbox already holding an address claim).
    /// </summary>
    /// <remarks>
    /// The alternative is clients fishing data out of <c>detail</c>'s prose — which is English regardless of
    /// the user's language (issue #424), so a client that needs the FACT and not the sentence must get it as
    /// data and compose its own localized text.
    /// </remarks>
    public IReadOnlyDictionary<string, object?>? Extensions { get; }

    public ApiException(string errorCode, int statusCode, string message, IReadOnlyDictionary<string, object?>? extensions = null)
        : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
        Extensions = extensions;
    }
}
