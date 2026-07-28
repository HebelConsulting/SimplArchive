using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Ocr;

// Thrown when an OCR-language selection is empty or names a code outside the supported catalog. Shared by the
// tenant default-OCR-languages setting + the per-document OCR-languages endpoint (same UNKNOWN_OCR_LANGUAGE wire
// code); the factories preserve each message.
public sealed class UnknownOcrLanguageException : OcrException
{
    private UnknownOcrLanguageException(string message)
        : base("UNKNOWN_OCR_LANGUAGE", StatusCodes.Status400BadRequest, message)
    {
    }

    public static UnknownOcrLanguageException Required() =>
        new("At least one OCR language is required.");

    public static UnknownOcrLanguageException Unsupported(string language) =>
        new($"'{language}' is not a supported OCR language.");
}
