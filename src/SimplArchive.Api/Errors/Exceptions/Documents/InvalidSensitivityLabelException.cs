using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// Thrown when a sensitivity-label value isn't one of the defined levels (ADR "Data classification / sensitivity
// labels").
public sealed class InvalidSensitivityLabelException : DocumentException
{
    public InvalidSensitivityLabelException()
        : base("INVALID_SENSITIVITY_LABEL", StatusCodes.Status400BadRequest, "The sensitivity label is not a recognized level.")
    {
    }
}
