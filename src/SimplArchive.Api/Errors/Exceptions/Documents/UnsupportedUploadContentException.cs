using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

/// <summary>
/// The uploaded content is something the archive does not store (ADR 0718) — an executable or a script, judged
/// by its bytes as well as its name.
/// </summary>
/// <remarks>
/// 415 rather than 400: the request is well-formed and the caller did nothing wrong procedurally; it is the
/// media that is unsupported, which is exactly what the status means. The reason travels in the message so the
/// person who uploaded it knows what to do differently — a refusal that says only "no" invites a retry.
/// </remarks>
public sealed class UnsupportedUploadContentException : DocumentException
{
    public UnsupportedUploadContentException(string reason)
        : base(
            "UNSUPPORTED_UPLOAD_CONTENT",
            StatusCodes.Status415UnsupportedMediaType,
            $"This content is not archived: {reason}.")
    {
    }
}
