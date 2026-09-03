namespace SimplArchive.ModuleAbi;

/// <summary>
/// A module's source of named facts (ADRs 0736/0742): the aggregate half of the state machine's two
/// predicate families — <c>landingsLast90Days</c>, <c>hoursSinceCheck</c> — computed from the module's own
/// read models, never authoritative, always rebuildable from documents.
/// </summary>
/// <remarks>
/// <para>
/// Registered by the module in <see cref="IIndustryModule.ConfigureServices"/>; the engine resolves
/// providers by <see cref="FactNames"/>, caches per evaluation, and includes each fact's VALUE in every
/// explanation — a red gate is a diagnosis ("4 landings in the last 90 days, 12 required"), never a bare
/// verdict.
/// </para>
/// <para>
/// Facts and proposal queries run under the module's SERVICE PRINCIPAL, whose rights the tenant admin
/// granted at activation (ADR 0736) — never under the asking caller, which would defeat proposals for
/// exactly the person they exist to help. Results are filtered to what the answer needs.
/// </para>
/// <para>
/// Providers must not push: reacting to the world belongs to transitions (ADR 0737), and a provider that
/// polls or scans for changes is the forbidden second event seam wearing a disguise.
/// </para>
/// </remarks>
public interface IModuleFactProvider
{
    /// <summary>The fact names this provider answers, referenced by the state-machine definition.</summary>
    IReadOnlyList<string> FactNames { get; }

    /// <summary>
    /// Computes one fact for one subject. <paramref name="subjectDocumentId"/> is the subject's document
    /// (a dossier, an aircraft); <paramref name="asOf"/> makes evaluation deterministic given documents +
    /// facts + clock, which is what makes a machine testable without a host (ADR 0742).
    /// </summary>
    Task<FactValue> GetAsync(string factName, Guid subjectDocumentId, DateTimeOffset asOf, CancellationToken cancellationToken = default);
}

/// <summary>A computed fact: the value plus the display text the explanation carries.</summary>
/// <param name="Value">The comparable value (number, bool, date) as its invariant string.</param>
/// <param name="DisplayText">What the explanation shows for this fact — the diagnosis half.</param>
public sealed record FactValue(string Value, string DisplayText);
