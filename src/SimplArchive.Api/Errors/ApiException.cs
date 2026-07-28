namespace SimplArchive.Api.Errors;

// Lets application code throw with a specific, stable errorCode rather than a generic unhandled
// exception — the global exception handler (see ADR "Hypermedia envelope and Problem Details errors
// (foundation slice)") translates this into an RFC 7807 Problem Details response carrying ErrorCode as
// the `errorCode` extension member and StatusCode as the HTTP status.
public class ApiException : Exception
{
    public string ErrorCode { get; }

    public int StatusCode { get; }

    public ApiException(string errorCode, int statusCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }
}
