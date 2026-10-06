using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Modules;

/// <summary>
/// Who may invoke a machine transition: ONE answer, asked by the link builder and by the POST alike, so the link
/// never promises what the call refuses (ADR 0543).
/// </summary>
/// <remarks>
/// Three gates, by declaration:
/// <list type="bullet">
/// <item>the populate hook (ADR 0764): an automated act the viewer merely triggers, so SEEING is the whole ask;</item>
/// <item>a principal-invoked act (ABI 1.6, ADR 0897): the person the subject's fields name, if they can see it, or a
/// tenant administrator. It REPLACES the edit gate: an editor who is not that person is not offered the act;</item>
/// <item>every other act mutates on the caller's behalf, so it needs <c>CanEditContent</c>.</item>
/// </list>
/// Scoped: the caller's admin flag and e-mail are read once per request, however many transitions a resource lists.
/// </remarks>
public sealed class TransitionInvocationRule(
    SimplArchiveDbContext dbContext, DocumentAccessService access, ICurrentUserAccessor currentUser)
{
    private bool? _isTenantAdmin;
    private string? _callerEmail;
    private bool _callerEmailRead;

    public async Task<bool> MayInvokeAsync(
        StateMachineCatalog.TransitionDefinition transition, Guid documentId, EffectiveRights rights,
        CancellationToken cancellationToken)
    {
        if (transition.AutoRefreshOnOpen)
        {
            return rights.CanSee;
        }

        if (transition.InvokedByPrincipalFields is not { Count: > 0 } fields)
        {
            return rights.CanEditContent;
        }

        if (!rights.CanSee)
        {
            return false;
        }

        _isTenantAdmin ??= await access.IsTenantAdminAsync(cancellationToken);
        if (_isTenantAdmin.Value)
        {
            return true;
        }

        // A user only: a service account has no e-mail here and is never "that person".
        var caller = await CallerNormalizedEmailAsync(cancellationToken);
        if (caller is null)
        {
            return false;
        }

        var named = await NamedPrincipalAsync(documentId, fields, cancellationToken);
        return named is not null && string.Equals(named, caller, StringComparison.Ordinal);
    }

    /// <summary>The first declared field holding a value, normalised as <c>User.NormalizedEmail</c> is.</summary>
    private async Task<string?> NamedPrincipalAsync(Guid documentId, IReadOnlyList<string> fields, CancellationToken cancellationToken)
    {
        // The document's CURRENT mask version only: a value left behind under an earlier version names nobody now.
        var values = await dbContext.Documents
            .Where(d => d.Id == documentId)
            .Join(dbContext.FieldDefinitions, d => d.MaskVersionId, f => (Guid?)f.MaskVersionId, (d, f) => f)
            .Where(f => fields.Contains(f.Name))
            .Join(dbContext.FieldValues.Where(v => v.DocumentId == documentId),
                f => f.Id, v => v.FieldDefinitionId, (f, v) => new { f.Name, v.Value, v.Ordinal })
            .ToListAsync(cancellationToken);

        return fields
            .Select(field => values
                .Where(v => v.Name == field && !string.IsNullOrWhiteSpace(v.Value))
                .OrderBy(v => v.Ordinal)
                .Select(v => v.Value.Trim().ToUpperInvariant())
                .FirstOrDefault())
            .FirstOrDefault(value => value is not null);
    }

    private async Task<string?> CallerNormalizedEmailAsync(CancellationToken cancellationToken)
    {
        if (!_callerEmailRead)
        {
            _callerEmailRead = true;
            _callerEmail = currentUser.UserId is { } userId
                ? await dbContext.Users.Where(u => u.Id == userId).Select(u => u.NormalizedEmail).FirstOrDefaultAsync(cancellationToken)
                : null;
        }

        return _callerEmail;
    }
}
