using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// A supplied version FiledAt (ADR 0520) that can't be parsed as a date/time.
public sealed class InvalidFilingDateException : DocumentException
{
    public InvalidFilingDateException(string message)
        : base("INVALID_FILING_DATE", StatusCodes.Status400BadRequest, message)
    {
    }
}
