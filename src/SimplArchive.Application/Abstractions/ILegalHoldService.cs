namespace SimplArchive.Application.Abstractions;

// Answers "is this document frozen by a legal hold?" (ADR "Legal hold & retention enforcement"). A document is
// frozen if it — or any ancestor — is covered by an ACTIVE hold; enforcement sites (delete/move/rename/version/
// metadata) call this before mutating. Implemented over an ancestor walk (like ACL inheritance).
public interface ILegalHoldService
{
    // True if documentId, or any of its ancestors, is in an active legal hold.
    Task<bool> IsFrozenAsync(Guid documentId, CancellationToken cancellationToken = default);

    // True if any of the given documents is DIRECTLY in an active hold. Used by the delete path, which already
    // enumerates the subtree it would cascade-delete: combined with IsFrozenAsync(root) this refuses the delete
    // when the target is under an ancestor hold or any descendant is itself held.
    Task<bool> AnyDirectlyHeldAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default);
}
