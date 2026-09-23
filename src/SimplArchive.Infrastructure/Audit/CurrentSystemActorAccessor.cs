namespace SimplArchive.Infrastructure.Audit;

/// <summary>
/// A scope's SYSTEM-actor fallback for audit resolution (#1329): set by a processing scope that acts for
/// no principal — a department-mailbox LMTP delivery is the founding case — so the generic
/// <c>RecordAsync</c> calls deep inside shared code (the finalizer's refused attachment, a classified
/// contact) resolve to <c>System/Guid.Empty/&lt;name&gt;</c> instead of being warn-dropped. Deliberately a
/// NAME, not a flag: the event should say what kind of system act this was ("Inbound mail"), the same way
/// <c>RecordForActorAsync(System, …)</c> callers do. Scoped and null by default — an unset scope keeps
/// exactly the #1312 behaviour (warn + drop), so nothing becomes quietly attributable to "System".
/// </summary>
public sealed class CurrentSystemActorAccessor
{
    public string? Name { get; set; }
}
