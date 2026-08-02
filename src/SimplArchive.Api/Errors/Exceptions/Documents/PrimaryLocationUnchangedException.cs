using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// The requested primary-location folder is already the document's real parent — there is nothing to promote
// (ADR 0506).
public sealed class PrimaryLocationUnchangedException : DocumentException
{
    public PrimaryLocationUnchangedException()
        : base("PRIMARY_LOCATION_UNCHANGED", StatusCodes.Status409Conflict,
            "The document already lives in that folder.")
    {
    }
}
