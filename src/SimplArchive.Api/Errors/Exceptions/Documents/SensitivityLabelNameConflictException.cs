using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// A sensitivity label with that name already exists in the tenant (ADR "Configurable sensitivity labels +
// upload defaults") — the (TenantId, Name) unique index.
public sealed class SensitivityLabelNameConflictException : DocumentException
{
    public SensitivityLabelNameConflictException()
        : base("SENSITIVITY_LABEL_NAME_CONFLICT", StatusCodes.Status409Conflict, "A sensitivity label with that name already exists.")
    {
    }
}
