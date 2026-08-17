using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// A section is a folder, and a folder with no name is not something the tree can show (#564).
public sealed class SectionNameRequiredException : DocumentException
{
    public SectionNameRequiredException()
        : base("SECTION_NAME_REQUIRED", StatusCodes.Status400BadRequest, "A section needs a name.")
    {
    }
}
