using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

/// <summary>
/// Something already in the archive was asked to move INTO an ephemeral mail folder (#633).
/// </summary>
/// <remarks>
/// Refused rather than re-keyed backwards. Filing out of the inbox is a one-way crossing: a document that has
/// acquired archive semantics — retention, disposition review, WORM, the recycle bin — cannot give them up by
/// being dragged back, and moving its bytes onto the ephemeral prefix would put them where the sweep deletes
/// them (ADR 0628).
/// </remarks>
public sealed class CannotFileIntoEphemeralMailException : DocumentException
{
    public CannotFileIntoEphemeralMailException()
        : base(
            "CANNOT_FILE_INTO_EPHEMERAL_MAIL",
            StatusCodes.Status409Conflict,
            "An archived document cannot be moved into a mail inbox — mail storage is temporary, and filing out "
            + "of it is one-way. Use a reference (shortcut) instead.")
    {
    }
}
