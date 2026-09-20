namespace SimplArchive.ModuleAbi;

/// <summary>
/// Whether a read over a PROTOCOL surface — a WebDAV <c>PROPFIND</c>, an IMAP <c>SELECT</c> — may invoke a
/// populate-on-open hook (ABI 0.27, core issue #1286).
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this exists for.</b> The populate-on-open hook is invoked by the two SimplArchive clients
/// and by nothing else. WebDAV and IMAP browse the same tree and never trigger it — while the ephemeral sweep
/// keeps purging expired content on schedule. So a user who reaches the archive only over a mounted drive or a
/// mail client ends up with an <b>empty</b> weather folder rather than a stale one: no error, no placeholder,
/// nothing saying the content is fetched on demand by a mechanism that protocol has no way to run.
/// </para>
/// <para>
/// <b>Why it is the MODULE's declaration and not simply "on".</b> ADR 0756 rejected a scheduled pre-fetch on
/// legal grounds — unattended retrieval of a third-party artefact — which is the whole reason the fetch is
/// lazy and user-triggered. Whether a given source may be fetched on a protocol read is a question about
/// <i>that source</i>: a public weather report and a licensed data feed are not the same answer, and only the
/// module author knows which they are holding. So the hook declares its own eligibility.
/// </para>
/// <para>
/// <b>And why eligibility is not the whole gate.</b> A declaration says the source <i>may</i> be fetched that
/// way; it does not say that in a particular tenant a <c>PROPFIND</c> <i>is</i> a person asking. WebDAV clients
/// enumerate on their own schedule — indexers, thumbnailers, a file-manager window left open — so treating
/// every read as user-initiated could quietly become the scheduled scraper that was rejected. That judgement
/// is about the tenant's own users, so the <b>tenant administrator</b> makes it, per tenant, through an
/// ordinary module setting. Eligible AND enabled, or no fetch.
/// </para>
/// </remarks>
public enum ProtocolReadRefresh
{
    /// <summary>
    /// A protocol read never invokes the hook. The default, and the safe direction: an existing module keeps
    /// exactly today's behaviour, and a module author who has not considered the question does not answer it
    /// by accident.
    /// </summary>
    Never = 0,

    /// <summary>
    /// The source MAY be fetched on a protocol read, if the tenant administrator has enabled it. The host
    /// surfaces the per-tenant toggle automatically for a machine declaring this; the module ships no UI and
    /// writes no storage code, exactly as for any other module setting (ADR 0772).
    /// </summary>
    WhenTenantEnables = 1,
}

/// <summary>The setting the host surfaces for a module whose hooks declare
/// <see cref="ProtocolReadRefresh.WhenTenantEnables"/>.</summary>
/// <remarks>
/// <para>
/// The key is defined <b>here</b> rather than by each module, because the host is what renders the toggle,
/// stores the answer and reads it back on a <c>PROPFIND</c> — a module that invented its own key would be
/// declaring a setting the host then had to guess the name of. It is nevertheless an ordinary per-module
/// setting row (ADR 0772), scoped to the declaring module and answered per tenant: two active modules get two
/// independent toggles, which is the right shape, since eligibility is a fact about a module's own sources.
/// </para>
/// <para>
/// A module's seeder may write it — the flight-school demo does, so a fresh tenant browses a mounted drive and
/// sees weather rather than an empty folder — but writing it is a <i>default</i>, not a lock: the tenant
/// administrator can turn it off again, and an absent row reads as <b>off</b>.
/// </para>
/// </remarks>
public static class ProtocolReadRefreshSetting
{
    /// <summary>The setting key. Part of the stored row's identity — renaming it strands every answer.</summary>
    public const string Key = "core.protocolReadRefresh";
}
