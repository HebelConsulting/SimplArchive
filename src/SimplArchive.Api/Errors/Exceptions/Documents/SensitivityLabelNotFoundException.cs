using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// The referenced sensitivity label doesn't exist in the tenant (ADR "Configurable sensitivity labels + upload
// defaults").
public sealed class SensitivityLabelNotFoundException : DocumentException
{
    public SensitivityLabelNotFoundException()
        : base("SENSITIVITY_LABEL_NOT_FOUND", StatusCodes.Status404NotFound, "The sensitivity label was not found.")
    {
    }
}
