using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

public sealed class NoFileException : DocumentException
{
    public NoFileException()
        : base("NO_FILE", StatusCodes.Status400BadRequest, "An archive file is required.")
    {
    }
}
