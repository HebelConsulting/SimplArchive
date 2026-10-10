namespace SimplArchive.ApiClient;

// Raised for an Api action that failed with a message worth showing the user (duplicate name, no permission).
// Base for a real error condition surfaced by SimplArchiveApiClient — carries a user-facing message the crash
// guard / status line displays. No longer sealed: intent-named conditions subclass it (per CLAUDE.md's
// exception-type rule) while the existing catch(ApiActionException) surfacing still picks them up.
public class ApiActionException(string message) : Exception(message);

// Set-primary-location / promote-a-reference errors (ADR 0506) — a small family under ApiActionException so a
// caller can catch the whole group and the status line still shows the message. Each type fixes its own message.
public class PrimaryLocationException(string message) : ApiActionException(message);

public sealed class CannotSetPrimaryLocationException()
    : PrimaryLocationException("Can't set that folder as the primary location.");

public sealed class SetPrimaryLocationForbiddenException()
    : PrimaryLocationException("You don't have permission to change this item's primary location.");

public sealed class PrimaryLocationConcurrencyException()
    : PrimaryLocationException("This item changed since you loaded it — refresh and try again.");

// A dropped file whose name is already used in the target folder. Its own type rather than the string-message
// ApiActionException it replaces, because this one condition is RECOVERABLE — the caller asks the user what they
// meant (a new version of what is there, or a new document under another name) instead of only reporting it.
// Carries the name so the prompt can name the file without re-deriving it.
public sealed class DocumentNameTakenException(string fileName)
    : ApiActionException($"'{fileName}': a document with that name already exists here.")
{
    public string FileName { get; } = fileName;
}

// Raised by DeleteUserAsync when the user still holds pending review tasks and no replacement reviewer was
// supplied (ADR "Workflow review reassignment") — the caller (Users & groups tab) prompts for a replacement
// and retries with reassignReviewsTo.
public sealed class ReviewerHasPendingReviewsException(string message) : Exception(message);

// 409 DUPLICATE_ADDRESS_CLAIM (#703): the address is on another mailbox's list, and the message names it.
// Its own type because it is a QUESTION, not a failure — the caller asks the admin and retries with
// confirmDuplicateClaims rather than reporting an error.
public sealed class DuplicateAddressClaimException(string message) : Exception(message);

/// <summary>
/// Somebody else wrote the document while this form was open — the 412 the combined detail save can now
/// actually raise (ADR 0794), because its precondition is the tag the form was LOADED with.
/// </summary>
/// <remarks>
/// Its own type rather than a bare <see cref="ApiActionException"/> so the pane can offer the one action that
/// helps — reload and try again — instead of reporting it as a save that merely failed.
/// </remarks>
public sealed class DetailChangedElsewhereException(string message) : Exception(message);
