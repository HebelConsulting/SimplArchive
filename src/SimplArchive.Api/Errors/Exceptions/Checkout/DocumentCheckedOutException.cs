using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Checkout;

// The cross-cutting edit lock: a document is checked out by another user, so a mutation is refused (ADR "Document
// check-out / check-in"). Thrown at every mutation site — rename/mask/index/move, new version, inbox
// file-as-version — plus the delete variant. All share the DOCUMENT_CHECKED_OUT wire code; the default ctor
// covers the common "cannot be changed" case and ForDeletion() covers the delete variant.
public sealed class DocumentCheckedOutException : CheckoutException
{
    public DocumentCheckedOutException(string message = "This document is checked out by another user and cannot be changed until it is checked in.")
        : base("DOCUMENT_CHECKED_OUT", StatusCodes.Status409Conflict, message)
    {
    }

    public static DocumentCheckedOutException ForDeletion() =>
        new("This document (or an item within it) is checked out by another user and cannot be deleted.");

    // A checked-out document is being actively edited (a check-in will produce a new version), so it can't be
    // submitted for review — the check-out must be resolved first (ADR "Workflow / check-out interaction").
    // Blocks regardless of who holds the lock, so the message doesn't say "by another user".
    public static DocumentCheckedOutException ForSubmit() =>
        new("This document is checked out, so it cannot be submitted for review — check it in or discard the check-out first.");
}
