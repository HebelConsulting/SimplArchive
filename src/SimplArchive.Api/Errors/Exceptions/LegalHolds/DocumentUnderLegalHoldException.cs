using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.LegalHolds;

// The cross-cutting freeze: a document (or an item within it) is under an active legal hold, so a mutation is
// refused (ADR "Legal hold and retention enforcement"). Thrown at every mutation site — rename/mask/index/move,
// new version, check-in, inbox file-as-version — plus the delete and purge variants. All share the LEGAL_HOLD
// wire code; the default ctor covers the common "cannot be changed" case and the factories cover delete/purge.
public sealed class DocumentUnderLegalHoldException : LegalHoldException
{
    public DocumentUnderLegalHoldException(string message = "This document is under a legal hold and cannot be changed.")
        : base("LEGAL_HOLD", StatusCodes.Status409Conflict, message)
    {
    }

    public static DocumentUnderLegalHoldException ForDeletion() =>
        new("This document (or an item within it) is under a legal hold and cannot be deleted.");

    public static DocumentUnderLegalHoldException ForPurge() =>
        new("This item is under a legal hold and cannot be purged.");
}
