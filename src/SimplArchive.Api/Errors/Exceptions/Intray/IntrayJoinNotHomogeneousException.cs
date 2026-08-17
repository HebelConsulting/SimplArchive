using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Intray;

// Thrown when a join mixes formats — a PDF with a TIFF (#487). Both honest ways to combine them (rasterise the
// PDF into the TIFF, or wrap the TIFF into the PDF) silently change what the pages are: resolution, colour, and
// whether the text is still text. Declining is better than converting a scan behind the user's back.
public sealed class IntrayJoinNotHomogeneousException : IntrayException
{
    public IntrayJoinNotHomogeneousException()
        : base(
            "INTRAY_JOIN_NOT_HOMOGENEOUS",
            StatusCodes.Status400BadRequest,
            "All the items being joined must be the same format — either all PDF or all TIFF.")
    {
    }
}
