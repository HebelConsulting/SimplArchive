using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

/// <summary>A signed version cannot be OCR'd — the conversion would break the signature (#999).</summary>
public sealed class SignedVersionNotConvertibleException : DocumentException
{
    public SignedVersionNotConvertibleException()
        : base("SIGNED_VERSION_NOT_CONVERTIBLE", StatusCodes.Status409Conflict,
            "This version is digitally signed; OCR would break the signature, so it cannot be made searchable.")
    {
    }
}
