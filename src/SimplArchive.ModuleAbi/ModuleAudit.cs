namespace SimplArchive.ModuleAbi;

/// <summary>
/// A module's NAMED audit events (ABI 1.9, core ADR 0900): a sentence the generic floor cannot say, such as "pull
/// credential issued for ACME AG". Inject it into a controller or a handler.
/// </summary>
/// <remarks>
/// <para>
/// The floor already records every write a module makes through the facade, so nothing depends on this being
/// called; it adds meaning, it does not provide coverage. Record AFTER the act succeeded: in a transition the event
/// joins the engine's transaction and disappears with a rollback.
/// </para>
/// <para><b>Never put a secret in <c>detail</c></b>: the audit trail is exported, streamed and kept for years.</para>
/// </remarks>
public interface IModuleAudit
{
    /// <summary>Records <c>{moduleId}.{action}</c>, attributed to the request's caller (or to the module, in a
    /// background sweep).</summary>
    /// <param name="action">A stable, module-local PascalCase name (<c>CredentialIssued</c>); the core prefixes the
    /// module id. Refused if it is not one word of letters and digits, up to 64 characters.</param>
    /// <param name="documentId">The document the act concerns, if any.</param>
    /// <param name="detail">A short sentence for the reader of the trail, at most 500 characters. Never a secret.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task RecordAsync(string action, Guid? documentId = null, string? detail = null, CancellationToken cancellationToken = default);
}
