using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// Backdating a version's filing date (a FiledAt earlier than today, ADR 0520) requires the CanImport right, since
// it affects the object-key year, the filing timestamp, and audit ordering.
public sealed class FilingDateBackdatingRequiresImportRightException : DocumentException
{
    public FilingDateBackdatingRequiresImportRightException()
        : base("IMPORT_RIGHT_REQUIRED_TO_BACKDATE", StatusCodes.Status403Forbidden,
            "Backdating a document's filing date requires the import right.")
    {
    }
}
