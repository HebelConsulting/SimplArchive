using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Manuals;

/// <summary>
/// What changing the tenant's standard repository does beyond the setting itself (ADR 0892): the manuals folder moves
/// with it. Runs INSIDE the tenant-settings PUT's verb contract, so it commits with the setting or not at all, under the
/// precondition the administrator sent for the tenant — never on its own.
/// </summary>
public static class StandardRepositoryChange
{
    /// <summary>
    /// Moves the manuals folder directly under <paramref name="standardRepositoryId"/>, if there is one and it is not
    /// already there. Does not save — the caller commits it together with the change of standard repository.
    /// </summary>
    public static async Task MoveUnderAsync(SimplArchiveDbContext dbContext, Guid standardRepositoryId, CancellationToken cancellationToken)
    {
        if (await ManualsFolder.FindAsync(dbContext, cancellationToken) is { } folder && folder.ParentId != standardRepositoryId)
        {
            folder.ParentId = standardRepositoryId;
            folder.Name = await ManualsFolder.FreeNameAsync(dbContext, standardRepositoryId, cancellationToken, keep: folder.Name);
        }
    }

}
