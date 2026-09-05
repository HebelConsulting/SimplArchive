using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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

    /// <summary>One transition: the button caption, the guard, the module's handler.</summary>
    public sealed record TransitionDefinition(string Label, IReadOnlyList<StateCondition> Guard, Func<TransitionContext, Task> Handler);

    /// <summary>One declared machine: whose it is, its subject mask, its statuses, its transitions.</summary>
    /// <remarks><see cref="ModuleId"/> is what the wire surface gates activation on (ADR 0737): a
    /// machine's transitions exist for a tenant exactly when its declaring module is active there. Null
    /// only for machines declared straight on the catalog (tests) — nothing gates those.</remarks>
    public sealed record MachineDefinition(
        string MachineId,
        string? ModuleId,
        Guid SubjectMaskId,
        Dictionary<string, IReadOnlyList<StateCondition>> Statuses,
        Dictionary<string, TransitionDefinition> Transitions,
        // statusName → the module's escalation handler (ABI 0.5): what the background sweep invokes while that
        // status holds to learn who to remind and what to say.
        Dictionary<string, Func<TransitionContext, Task<IReadOnlyList<EscalationNotice>>>> Escalations);

    public IReadOnlyDictionary<string, MachineDefinition> Machines => _machines;

    public IStateMachineBuilder Machine(string machineId, Guid subjectMaskId) =>
        Machine(machineId, subjectMaskId, moduleId: null);

    /// <summary>The loader's entry: definitions declared through this carry the declaring module's id.</summary>
    public IStateMachineDefinitions ForModule(string moduleId) => new ModuleScope(this, moduleId);

    private IStateMachineBuilder Machine(string machineId, Guid subjectMaskId, string? moduleId)
    {
        var definition = new MachineDefinition(machineId, moduleId, subjectMaskId,
            new Dictionary<string, IReadOnlyList<StateCondition>>(StringComparer.Ordinal),
            new Dictionary<string, TransitionDefinition>(StringComparer.Ordinal),
            new Dictionary<string, Func<TransitionContext, Task<IReadOnlyList<EscalationNotice>>>>(StringComparer.Ordinal));
        _machines[machineId] = definition;
        return new Builder(definition);
    }

    private sealed class ModuleScope(StateMachineCatalog catalog, string moduleId) : IStateMachineDefinitions
    {
        public IStateMachineBuilder Machine(string machineId, Guid subjectMaskId) =>
            catalog.Machine(machineId, subjectMaskId, moduleId);
    }

    private sealed class Builder(MachineDefinition definition) : IStateMachineBuilder
    {
        public IStateMachineBuilder Status(string name, params StateCondition[] conditions)
        {
            definition.Statuses[name] = conditions;
            return this;
        }

        public IStateMachineBuilder Transition(string name, string label, IReadOnlyList<StateCondition> guard, Func<TransitionContext, Task> handler)
        {
            definition.Transitions[name] = new TransitionDefinition(label, guard, handler);
            return this;
        }

        public IStateMachineBuilder Escalates(string statusName, Func<TransitionContext, Task<IReadOnlyList<EscalationNotice>>> handler)
        {
            definition.Escalations[statusName] = handler;
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
    private readonly ModuleReadModelCatalog _readModels;

    private readonly ModuleIdentityAccessor? _identity;

    public StateMachineEngine(
        SimplArchiveDbContext dbContext, StateMachineCatalog catalog, IModuleArchiveFacade archive,
        IServiceProvider services, ModuleReadModelCatalog? readModels = null, ModuleIdentityAccessor? identity = null)
    {
        _dbContext = dbContext;
        _catalog = catalog;
        _archive = archive;
        _services = services;
        _readModels = readModels ?? ModuleReadModelCatalog.Empty;
        _identity = identity;
    }

    /// <summary>The machine's module becomes the scope's acting module (ADR 0736): every facade read the
    /// evaluation or a handler makes is gated by THAT module's principal — its consented grants.</summary>
    private void ActAs(StateMachineCatalog.MachineDefinition machine)
    {
        if (_identity is not null)
        {
            _identity.ModuleId = machine.ModuleId;
        }
    }

    public async Task<StatusResult> EvaluateStatusAsync(string machineId, string statusName, Guid subjectDocumentId, DateTimeOffset asOf, CancellationToken cancellationToken = default)
    {
        var machine = Require(machineId);
        ActAs(machine);
        if (!machine.Statuses.TryGetValue(statusName, out var conditions))
        {
            throw new ArgumentException($"Machine '{machineId}' declares no status '{statusName}'.", nameof(statusName));
        }

        return await EvaluateAsync(conditions, subjectDocumentId, asOf, cancellationToken);
    }

    /// <summary>Runs a transition: guard first, handler only on green. The refusal IS the explanation.</summary>
    /// <remarks>
    /// The ENGINE owns the transaction (ADR 0737): the handler's facade writes — each a SaveChanges of its
    /// own — join one database transaction, committed only when the handler returns. A handler that throws
    /// rolls the whole act back, so a half-performed transition is a state the archive cannot hold; the
    /// exception itself propagates, refusal semantics for free.
    /// </remarks>
    public async Task<StatusResult> ExecuteTransitionAsync(string machineId, string transitionName, Guid subjectDocumentId, DateTimeOffset asOf, CancellationToken cancellationToken = default)
    {
        var machine = Require(machineId);
        ActAs(machine);
        if (!machine.Transitions.TryGetValue(transitionName, out var transition))
        {
            throw new ArgumentException($"Machine '{machineId}' declares no transition '{transitionName}'.", nameof(transitionName));
        }

        var verdict = await EvaluateAsync(transition.Guard, subjectDocumentId, asOf, cancellationToken);
        if (!verdict.Satisfied)
        {
            return verdict;
        }

        // The act's one transaction (ADRs 0737/0738): the handler's document + projection writes are one
        // commit, and one rollback when it throws (InEngineTransactionAsync owns the enlist/rollback).
        await InEngineTransactionAsync(async () =>
        {
            await transition.Handler(new TransitionContext(subjectDocumentId, _archive, _services));
            return true;
        }, cancellationToken);

        return verdict;
    }

    /// <summary>
    /// Evaluates a status and, WHILE it holds, runs the module's escalation handler for it (ABI 0.5): the
    /// same act-as-the-module + engine-owned-transaction contract as a transition, so the handler's OWN
    /// idempotency-marker write commits (or rolls back with a throw). Returns the notices the module wants
    /// delivered — empty when the status does not hold (the subject is not in that state) or the module has
    /// already warned (its handler returns empty). The core delivers what comes back; it never composes the
    /// message or picks the audience (ADR 0736: who-and-what is the module's, the arithmetic is the engine's).
    /// </summary>
    public async Task<IReadOnlyList<EscalationNotice>> ExecuteEscalationAsync(string machineId, string statusName, Guid subjectDocumentId, DateTimeOffset asOf, CancellationToken cancellationToken = default)
    {
        var machine = Require(machineId);
        ActAs(machine);
        if (!machine.Escalations.TryGetValue(statusName, out var handler))
        {
            throw new ArgumentException($"Machine '{machineId}' declares no escalation on status '{statusName}'.", nameof(statusName));
        }

        if (!machine.Statuses.TryGetValue(statusName, out var conditions))
        {
            throw new ArgumentException($"Machine '{machineId}' declares no status '{statusName}'.", nameof(statusName));
        }

        // The status is the gate: only a subject actually in that state escalates.
        if (!(await EvaluateAsync(conditions, subjectDocumentId, asOf, cancellationToken)).Satisfied)
        {
            return [];
        }

        return await InEngineTransactionAsync(
            () => handler(new TransitionContext(subjectDocumentId, _archive, _services)), cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="body"/> inside the engine-owned transaction (ADRs 0737/0738): every wired module
    /// read-model context shares the core connection, so enlisting them here is what makes a handler's document
    /// writes and its projection writes ONE commit — and one rollback when it throws. On a throw the change
    /// TRACKERS are also cleared: the database rolls back on dispose but the contexts would still hold the
    /// handler's writes as clean entities, so a later FindAsync would serve a phantom row and a later save could
    /// resurrect rolled-back state (found by the rollback test reading Count = 1 from a table that held nothing).
    /// </summary>
    private async Task<T> InEngineTransactionAsync<T>(Func<Task<T>> body, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var enlisted = new List<Microsoft.EntityFrameworkCore.DbContext>();
        foreach (var contextType in _readModels.ContextTypes)
        {
            var readModelContext = (Microsoft.EntityFrameworkCore.DbContext)_services.GetRequiredService(contextType);
            await readModelContext.Database.UseTransactionAsync(transaction.GetDbTransaction(), cancellationToken);
            enlisted.Add(readModelContext);
        }

        try
        {
            var result = await body();
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            _dbContext.ChangeTracker.Clear();
            foreach (var readModelContext in enlisted)
            {
                readModelContext.ChangeTracker.Clear();
            }

            throw;
        }
        finally
        {
            foreach (var readModelContext in enlisted)
            {
                await readModelContext.Database.UseTransactionAsync(null, CancellationToken.None);
            }
        }
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
            // The clean inverse of DateNotPast — a declared Lapsed rather than a handler-evaluated one (0.4).
            ConditionTest.DatePast =>
                value is not null
                && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var pastDate)
                && pastDate < asOf,
            // The approaching edge: not past AND within Operand days of now (0.4). A non-integer or absent
            // operand cannot describe a window, so it never holds rather than throwing.
            ConditionTest.DateWithinDays =>
                value is not null
                && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var soonDate)
                && operand is not null
                && int.TryParse(operand, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
                && soonDate >= asOf
                && soonDate <= asOf.AddDays(days),
            ConditionTest.Equals => value is not null && string.Equals(value, operand, StringComparison.Ordinal),
            ConditionTest.NotEquals => !string.Equals(value, operand, StringComparison.Ordinal),
            ConditionTest.AtLeast =>
                value is not null
                && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                && operand is not null
                && number >= decimal.Parse(operand, CultureInfo.InvariantCulture),
            // Filled means non-blank (ABI 0.2, #1014): a saved-then-cleared field leaves an empty row,
            // and "names a pilot" must not be satisfied by whitespace.
            ConditionTest.Present => !string.IsNullOrWhiteSpace(value),
            _ => false,
        };
    }
}
