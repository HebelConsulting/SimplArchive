namespace SimplArchive.Api.Errors.Exceptions.Ocr;

// Base class for OCR-language validation errors (ADRs "Per-tenant/per-version OCR languages" / "System fields and
// OCR-language picker"). Inherits from ApiException so the global handler translates it to an RFC 7807 response;
// concrete errors inherit from this so a caller can `catch (OcrException)`. See the exception-type principle in
// CLAUDE.md.
public abstract class OcrException : ApiException
{
    protected OcrException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
