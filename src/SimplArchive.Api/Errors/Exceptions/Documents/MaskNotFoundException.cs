using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

public sealed class MaskNotFoundException : DocumentException
{
    public MaskNotFoundException()
        : base("MASK_NOT_FOUND", StatusCodes.Status400BadRequest, "The specified mask does not exist.")
    {
    }
}
