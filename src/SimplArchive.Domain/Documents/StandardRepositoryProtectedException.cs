namespace SimplArchive.Domain.Documents;

/// <summary>
/// The tenant's standard repository (<c>Tenant.StandardRepositoryId</c>, ADR 0892) was asked to be deleted.
/// </summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/> so the existing boundary catches keep compiling, and is its
/// own type so the Api can tell it apart from the other invariants that share that base — reported as a name clash,
/// it would tell an administrator to rename something when the fix is to choose another standard repository.
/// </remarks>
public sealed class StandardRepositoryProtectedException : InvalidOperationException
{
    public StandardRepositoryProtectedException(string documentName)
        : base($"'{documentName}' is this tenant's standard repository and cannot be deleted: SimplArchive files what "
            + "it brings there, its manuals first among them. Make another repository the standard one first (tenant "
            + "settings), then delete this one.")
    {
    }
}
