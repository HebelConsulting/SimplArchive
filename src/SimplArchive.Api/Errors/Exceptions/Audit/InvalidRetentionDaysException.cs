using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Audit;

// Thrown when the audit retention window is set to a negative number of days (ADR "Audit trail — retention and
// purge").
public sealed class InvalidRetentionDaysException : AuditException
{
    public InvalidRetentionDaysException()
        : base("INVALID_RETENTION_DAYS", StatusCodes.Status400BadRequest, "Retention days must be zero (keep forever) or a positive number of days.")
    {
    }
}
