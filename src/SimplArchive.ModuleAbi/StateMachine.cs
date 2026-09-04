namespace SimplArchive.ModuleAbi;

/// <summary>
/// The root a module defines its state machines against (ADR 0742), handed to
/// <see cref="IIndustryModule.DefineStateMachines"/> at load. A machine is declared over a subject MASK —
/// its subjects are the documents wearing it (a dossier, an aircraft, a charter).
/// </summary>
public interface IStateMachineDefinitions
{
    /// <summary>Begins one machine: a stable id (activation and diagnostics name it) over the mask whose
    /// documents are its subjects.</summary>
    IStateMachineBuilder Machine(string machineId, Guid subjectMaskId);
}

/// <summary>
/// The fluent builder (ADR 0742): derived statuses and explicit acts, one grammar. Conditions are DATA —
/// the enumerable shape is what lets the core explain a red gate with codes and values instead of a bare
/// refusal — while transition handlers stay real code, running in the same save as their document writes.
/// </summary>
public interface IStateMachineBuilder
{
    /// <summary>
    /// A derived status: computed on every ask, never stored — which is how "re-evaluate whenever reality
    /// may have moved" falls out of the model, and how a certificate filed by hand during a deactivation
    /// window simply counts when the module returns (ADRs 0740/0742).
    /// </summary>
    IStateMachineBuilder Status(string name, params StateCondition[] conditions);

    /// <summary>
    /// An act: guarded, handled, explained, LABELED. The handler receives the facade and the subject and
    /// performs the act's document writes — inside one transaction the engine owns (ADR 0737): a handler
    /// that throws rolls the whole act back. The engine runs it only when every guard condition holds, and
    /// a refusal carries each failed condition's explanation (a diagnosis, not a verdict). The label is
    /// the button both clients render on the subject through the generic action surface (ADR 0743) —
    /// today one plain string; a per-culture factory is the recorded widening when the first real module
    /// needs localized captions.
    /// </summary>
    IStateMachineBuilder Transition(string name, string label, IReadOnlyList<StateCondition> guard, Func<TransitionContext, Task> handler);
}

/// <summary>What a transition's handler receives: the subject, the archive, and the request's services.</summary>
/// <param name="SubjectDocumentId">The document the machine was asked about.</param>
/// <param name="Archive">The facade — the handler's writes go through the same five operations and the
/// same invariants as everyone else's (ADR 0741).</param>
/// <param name="Services">The request scope, for the module's OWN registrations — above all its
/// read-model context (ADR 0738), whose writes here land in the same transaction as the document
/// writes because the engine enlisted it before invoking the handler.</param>
public sealed record TransitionContext(Guid SubjectDocumentId, IModuleArchiveFacade Archive, IServiceProvider Services);

/// <summary>
/// One enumerable condition — the two predicate families of ADR 0736 as data. Exactly one of
/// <see cref="Field"/>/<see cref="Fact"/> is set.
/// </summary>
/// <param name="FailCode">The stable machine-readable code an explanation carries (<c>fs.medical-expired</c>);
/// tests and integrations branch on it, and it never changes with the prose.</param>
/// <param name="FailText">The human sentence, already localized by the module (its resources, its
/// languages); <c>{value}</c> is replaced with the evaluated value so the sentence is a diagnosis.</param>
public sealed record StateCondition(string FailCode, string FailText)
{
    /// <summary>A document predicate — over the subject's own field, or over the newest child document
    /// wearing <see cref="DocumentFieldCondition.ChildMaskId"/> (the certificate-in-a-dossier shape).</summary>
    public DocumentFieldCondition? Field { get; init; }

    /// <summary>A fact predicate — the aggregate family, answered by the module's fact provider.</summary>
    public FactCondition? Fact { get; init; }

    /// <summary>A predicate over the subject document's own field.</summary>
    public static StateCondition SubjectField(string fieldName, ConditionTest test, string? operand, string failCode, string failText) =>
        new(failCode, failText) { Field = new DocumentFieldCondition(null, fieldName, test, operand) };

    /// <summary>A predicate over the newest child wearing a mask — the certificate-in-a-dossier shape.</summary>
    public static StateCondition ChildField(Guid childMaskId, string fieldName, ConditionTest test, string? operand, string failCode, string failText) =>
        new(failCode, failText) { Field = new DocumentFieldCondition(childMaskId, fieldName, test, operand) };

    /// <summary>An aggregate predicate: the named fact's numeric value is at least the minimum.</summary>
    public static StateCondition FactAtLeast(string factName, long minimum, string failCode, string failText) =>
        new(failCode, failText) { Fact = new FactCondition(factName, ConditionTest.AtLeast, minimum.ToString()) };
}

/// <summary>A predicate over a document field. Null <paramref name="ChildMaskId"/> = the subject's own
/// field; set = the NEWEST child wearing that mask (a dossier's current Medical). A missing document or
/// field fails the condition — absence of evidence is absence of the right.</summary>
public sealed record DocumentFieldCondition(Guid? ChildMaskId, string FieldName, ConditionTest Test, string? Operand);

/// <summary>A predicate over a named fact (ADR 0736's aggregate family).</summary>
public sealed record FactCondition(string FactName, ConditionTest Test, string? Operand);

/// <summary>The comparison vocabulary — small on purpose; slice 1's machines need no more, and every
/// addition is a deliberate, versioned act (ADR 0741).</summary>
public enum ConditionTest
{
    /// <summary>The value parses as a date on or after the evaluation instant — "not expired".</summary>
    DateNotPast,

    /// <summary>String equality against <c>Operand</c> (ordinal).</summary>
    Equals,

    /// <summary>String inequality against <c>Operand</c> (ordinal) — also holds when the field is absent
    /// (a checkbox never ticked is not the checked value).</summary>
    NotEquals,

    /// <summary>Numeric: value ≥ <c>Operand</c>.</summary>
    AtLeast,

    /// <summary>The field is filled: present and not blank (ABI 0.2, #1014). The primitive the marker-
    /// Boolean workaround stood in for — "this entry names a pilot" is a presence question, and
    /// <see cref="NotEquals"/> deliberately holds for an absent field, so it cannot ask it. Appended,
    /// per this enum's own append-only rule. Takes no operand.</summary>
    Present,
}

/// <summary>A status evaluation's answer: the verdict plus every failed condition's diagnosis.</summary>
public sealed record StatusResult(bool Satisfied, IReadOnlyList<ConditionExplanation> Failed);

/// <summary>One failed condition, explained (ADR 0742): the code, the evaluated value, the sentence.</summary>
public sealed record ConditionExplanation(string Code, string? Value, string Text);
