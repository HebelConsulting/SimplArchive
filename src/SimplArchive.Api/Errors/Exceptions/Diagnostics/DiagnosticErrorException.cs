using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Diagnostics;

// A deliberately-thrown error for the diagnostics endpoint that verifies the global RFC 7807 error handler.
public sealed class DiagnosticErrorException : DiagnosticException
{
    public DiagnosticErrorException()
        : base("DIAGNOSTIC_ERROR", StatusCodes.Status400BadRequest, "This is a deliberate diagnostic error.")
    {
    }
}
