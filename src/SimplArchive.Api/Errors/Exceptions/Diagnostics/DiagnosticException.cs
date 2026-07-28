namespace SimplArchive.Api.Errors.Exceptions.Diagnostics;

// Base class for the diagnostics test-error route (proves the RFC 7807 pipeline end-to-end). Inherits from
// ApiException; kept as a specific type for consistency with the exception-type principle in CLAUDE.md.
public abstract class DiagnosticException : ApiException
{
    protected DiagnosticException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
