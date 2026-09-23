using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Encryption;

// Thrown when a KEK generation is asked to retire while objects are still wrapped by it (ADR 0014).
// Retiring destroys the keypair, so every one of those objects would become permanently unreadable —
// the designed failure mode arriving by ACCIDENT, which is exactly what this refusal prevents. The count
// is in the message because "run the sweep first" is only actionable if the admin can see how far it got.
public sealed class KekRetirementRefusedException : EncryptionException
{
    public KekRetirementRefusedException(string generation, int remaining)
        : base("KEK_RETIREMENT_REFUSED", StatusCodes.Status409Conflict,
            $"{remaining} object(s) are still wrapped by '{generation}'. Re-wrap them first — retiring it "
            + "now would make every one of them permanently unreadable.")
    {
    }
}
