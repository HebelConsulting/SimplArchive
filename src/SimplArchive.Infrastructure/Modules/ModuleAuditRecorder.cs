using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// The host's side of <see cref="IModuleAudit"/> (ABI 1.9, ADR 0900): a named event, recorded as <c>{moduleId}.{action}</c>
/// for the module the request is acting as, through the ordinary recorder.
/// </summary>
public sealed partial class ModuleAuditRecorder(
    IAuditRecorder audit, ModuleIdentityAccessor identity, ICurrentTenantAccessor tenant,
    ICurrentUserAccessor user, ICurrentServiceAccountAccessor serviceAccount, SimplArchiveDbContext dbContext) : IModuleAudit
{
    private const int DetailLimit = 500;

    public async Task RecordAsync(string action, Guid? documentId = null, string? detail = null, CancellationToken cancellationToken = default)
    {
        if (!ActionName().IsMatch(action ?? string.Empty))
        {
            throw new ArgumentException($"A module audit action is one PascalCase word of letters and digits (got '{action}').", nameof(action));
        }

        var moduleId = identity.ModuleId
            ?? throw new InvalidOperationException("A module audit event was recorded with no module acting; the host sets the identity before module code runs.");
        var tenantId = tenant.TenantId
            ?? throw new InvalidOperationException("A module audit event was recorded with no tenant.");
        var targetName = documentId is { } id
            ? await dbContext.Documents.Where(d => d.Id == id).Select(d => d.Name).FirstOrDefaultAsync(cancellationToken)
            : null;
        var details = detail is { Length: > DetailLimit } ? detail[..DetailLimit] : detail;
        var code = $"{moduleId}.{action}";

        if (user.UserId is null && serviceAccount.ServiceAccountId is null)
        {
            await audit.RecordForActorAsync(AuditActorType.System, Guid.Empty, $"Module {moduleId}", tenantId,
                code, documentId is null ? null : "Document", documentId, targetName, details, cancellationToken);
            return;
        }

        await audit.RecordAsync(code, documentId is null ? null : "Document", documentId, targetName, details, tenantId, cancellationToken);
    }

    [GeneratedRegex("^[A-Z][A-Za-z0-9]{0,63}$")]
    private static partial Regex ActionName();
}
