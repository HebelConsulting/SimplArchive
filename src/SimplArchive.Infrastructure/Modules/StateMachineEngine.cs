using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// The definitions the loaded modules declared (ADR 0742) — one immutable catalog, built at startup by
/// handing each module the builder. A singleton: the definitions are code, fixed for the process's life.
/// </summary>
public sealed class StateMachineCatalog : IStateMachineDefinitions
{
    private readonly Dictionary<string, MachineDefinition> _machines = new(StringComparer.Ordinal);

    /// <summary>One declared machine: its subject mask, its ordered statuses, its transitions.</summary>
    public sealed record MachineDefinition(
        string MachineId,
        Guid SubjectMaskId,
        Dictionary<string, IReadOnlyList<StateCondition>> Statuses,
        Dictionary<string, (IReadOnlyList<StateCondition> Guard, Func<TransitionContext, Task> Handler)> Transitions);

    public IReadOnlyDictionary<string, MachineDefinition> Machines => _machines;

    public IStateMachineBuilder Machine(string machineId, Guid subjectMaskId)
    {
        var definition = new MachineDefinition(machineId, subjectMaskId,
            new Dictionary<string, IReadOnlyList<StateCondition>>(StringComparer.Ordinal),
            new Dictionary<string, (IReadOnlyList<StateCondition>, Func<TransitionContext, Task>)>(StringComparer.Ordinal));
        _machines[machineId] = definition;
        return new Builder(definition);
    }

    private sealed class Builder(MachineDefinition definition) : IStateMachineBuilder
    {
        public IStateMachineBuilder Status(string name, params StateCondition[] conditions)
        {
            definition.Statuses[name] = conditions;
            return this;
        }

        public IStateMachineBuilder Transition(string name, IReadOnlyList<StateCondition> guard, Func<TransitionContext, Task> handler)
        {
            definition.Transitions[name] = (guard, handler);
            return this;
        }
    }
}

/// <summary>
/// Evaluates and executes against the catalog (ADR 0742): statuses derive on every ask — nothing is
/// stored, so a certificate filed by hand during a deactivation window simply counts — and a transition
/// runs its handler only when every guard condition holds, refusals carrying each failed condition's code,
/// value and sentence. Deterministic given documents + facts + clock, which is what makes a machine
/// testable without a host.
/// </summary>
public sealed class StateMachineEngine
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly StateMachineCatalog _catalog;
    private readonly IModuleArchiveFacade _archive;
    private readonly IServiceProvider _services;

    public StateMachineEngine(SimplArchiveDbContext dbContext, StateMachineCatalog catalog, IModuleArchiveFacade archive, IServiceProvider services)
    {
        _dbContext = dbContext;
        _catalog = catalog;
        _archive = archive;
        _services = services;
    }

    public async Task<StatusResult> EvaluateStatusAsync(string machineId, string statusName, Guid subjectDocumentId, DateTimeOffset asOf, CancellationToken cancellationToken = default)
    {
        var machine = Require(machineId);
        if (!machine.Statuses.TryGetValue(statusName, out var conditions))
        {
            throw new ArgumentException($"Machine '{machineId}' declares no status '{statusName}'.", nameof(statusName));
        }

        return await EvaluateAsync(conditions, subjectDocumentId, asOf, cancellationToken);
    }

    /// <summary>Runs a transition: guard first, handler only on green. The refusal IS the explanation.</summary>
    public async Task<StatusResult> ExecuteTransitionAsync(string machineId, string transitionName, Guid subjectDocumentId, DateTimeOffset asOf, CancellationToken cancellationToken = default)
    {
        var machine = Require(machineId);
        if (!machine.Transitions.TryGetValue(transitionName, out var transition))
        {
            throw new ArgumentException($"Machine '{machineId}' declares no transition '{transitionName}'.", nameof(transitionName));
        }

        var verdict = await EvaluateAsync(transition.Guard, subjectDocumentId, asOf, cancellationToken);
        if (!verdict.Satisfied)
        {
            return verdict;
        }

        await transition.Handler(new TransitionContext(subjectDocumentId, _archive));
        return verdict;
    }

    private StateMachineCatalog.MachineDefinition Require(string machineId) =>
        _catalog.Machines.TryGetValue(machineId, out var machine)
            ? machine
            : throw new ArgumentException($"No module declared a machine '{machineId}'.", nameof(machineId));

    private async Task<StatusResult> EvaluateAsync(IReadOnlyList<StateCondition> conditions, Guid subjectDocumentId, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        var failed = new List<ConditionExplanation>();
        foreach (var condition in conditions)
        {
            var value = condition switch
            {
                { Field: { } field } => await FieldValueAsync(field, subjectDocumentId, cancellationToken),
                { Fact: { } fact } => await FactValueAsync(fact, subjectDocumentId, asOf, cancellationToken),
                _ => null,
            };

            if (!Holds(condition, value, asOf))
            {
                // The diagnosis: the code tests branch on, the VALUE that failed, the module's sentence
                // with the value substituted — never a bare refusal (ADR 0742).
                failed.Add(new ConditionExplanation(condition.FailCode, value,
                    condition.FailText.Replace("{value}", value ?? "—", StringComparison.Ordinal)));
            }
        }

        return new StatusResult(failed.Count == 0, failed);
    }

    private async Task<string?> FieldValueAsync(DocumentFieldCondition field, Guid subjectDocumentId, CancellationToken cancellationToken)
    {
        var documentId = subjectDocumentId;
        if (field.ChildMaskId is { } childMaskId)
        {
            // The NEWEST child wearing the mask — a dossier's current Medical is the last one filed, and
            // an older certificate beside it must not answer for it.
            var children = await _archive.GetChildrenAsync(subjectDocumentId, childMaskId, cancellationToken);
            if (children.Count == 0)
            {
                return null; // absence of evidence is absence of the right.
            }

            documentId = children[^1].Id;
        }

        var maskVersionId = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => d.MaskVersionId)
            .SingleOrDefaultAsync(cancellationToken);
        if (maskVersionId is null)
        {
            return null;
        }

        // By name within the document's own mask version — the facade's rule, applied to reads.
        return await _dbContext.FieldValues
            .Where(v => v.DocumentId == documentId)
            .Join(_dbContext.FieldDefinitions, v => v.FieldDefinitionId, f => f.Id, (v, f) => new { f.Name, v.Value })
            .Where(x => x.Name == field.FieldName)
            .Select(x => x.Value)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<string?> FactValueAsync(FactCondition fact, Guid subjectDocumentId, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        // Providers are resolved by name from whatever the modules registered (ADR 0736); an unclaimed
        // fact name is a definition bug and says so, rather than quietly evaluating to false.
        var provider = _services.GetServices<IModuleFactProvider>()
            .FirstOrDefault(p => p.FactNames.Contains(fact.FactName, StringComparer.Ordinal))
            ?? throw new InvalidOperationException($"No registered fact provider answers '{fact.FactName}'.");

        return (await provider.GetAsync(fact.FactName, subjectDocumentId, asOf, cancellationToken)).Value;
    }

    private static bool Holds(StateCondition condition, string? value, DateTimeOffset asOf)
    {
        // Branched explicitly, not null-coalesced: a field condition's operand is LEGITIMATELY null
        // (DateNotPast needs none), and `Field?.Operand ?? Fact!.Operand` walked into the null Fact
        // exactly then.
        var (test, operand) = condition.Field is { } field
            ? (field.Test, field.Operand)
            : (condition.Fact!.Test, condition.Fact.Operand);

        return test switch
        {
            ConditionTest.DateNotPast =>
                value is not null
                && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                && date >= asOf,
            ConditionTest.Equals => value is not null && string.Equals(value, operand, StringComparison.Ordinal),
            ConditionTest.NotEquals => !string.Equals(value, operand, StringComparison.Ordinal),
            ConditionTest.AtLeast =>
                value is not null
                && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                && operand is not null
                && number >= decimal.Parse(operand, CultureInfo.InvariantCulture),
            _ => false,
        };
    }
}
