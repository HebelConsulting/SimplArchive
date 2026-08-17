using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Intray;

// Thrown when a cut is asked for and the scan carries no separator sheets — or nothing but separator sheets
// (#492). Detection ran and answered; there is simply nothing to cut into.
//
// An error rather than a no-op because the caller pressed a button: a silent success that changes nothing is
// indistinguishable from a broken feature, and the most likely cause is worth saying out loud — the sheets were
// left out of the stack, or came through too crooked to read.
public sealed class IntrayNoPatchCodesFoundException : IntrayException
{
    public IntrayNoPatchCodesFoundException(string name)
        : base(
            "INTRAY_NO_PATCH_CODES_FOUND",
            StatusCodes.Status400BadRequest,
            $"No separator sheets were found in '{name}', so there is nothing to cut it into.")
    {
    }
}
