namespace SimplArchive.Api.Errors.Exceptions.Documents;

/// <summary>
/// A combined export (#658) was asked of a selection that cannot combine: mixed kinds, or items that are not
/// <c>.vcf</c>/<c>.ics</c>. The clients only offer the action on a uniform selection, so reaching this means a
/// raced selection change or a hand-built request — either way the caller needs the reason, not a file.
/// </summary>
public sealed class BulkExportNotCombinableException : DocumentException
{
    public BulkExportNotCombinableException()
        : base("BULK_EXPORT_NOT_COMBINABLE", StatusCodes.Status400BadRequest,
            "Only a selection of only contacts (.vcf) or only calendar entries (.ics) can be exported as one file.")
    {
    }
}
