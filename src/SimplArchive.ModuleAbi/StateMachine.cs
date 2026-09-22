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

    /// <summary>
    /// An escalation off a derived status (ABI 0.5): a core background sweep evaluates <paramref name="statusName"/>
    /// per subject and, WHILE it holds, invokes <paramref name="handler"/> to turn "this subject is in that
    /// state" into who to tell and what to say. The handler receives the same context a transition does — the
    /// subject and the facade — so it can walk the subject's own graph to resolve the audience (an enrollment's
    /// instructor lives up on the dossier, not on the enrollment) and read/write its OWN idempotency marker: it
    /// returns the notices to send the FIRST time and an empty list once it has recorded that it warned, so the
    /// sweep does not re-notify every tick. The core resolves each notice's recipient e-mail to a tenant user and
    /// files an in-app notification; a marker keyed to the deadline it warned about re-arms on renewal for free.
    /// The status arithmetic (is it Expiring?) stays the engine's; who-and-what stays the module's.
    /// </summary>
    IStateMachineBuilder Escalates(string statusName, Func<TransitionContext, Task<IReadOnlyList<EscalationNotice>>> handler);

    /// <summary>
    /// A transition the clients invoke AUTOMATICALLY when a subject is opened (ABI 0.6) — the populate-on-open
    /// hook. Same shape, handler and engine-owned transaction as <see cref="Transition"/> (it IS a transition,
    /// reachable by its label like any other), but flagged so the subject resource advertises it as
    /// auto-invoke and both clients run it the moment the folder is opened rather than waiting for a button.
    /// This is how "opening the folder shows today's DABS" works without inventing a second reactive hook: the
    /// open IS the lazy, user-triggered fetch. Idempotent by contract — it runs on every open, so the handler
    /// MUST no-op when nothing changed (the ETag / issue-time check), never blindly re-fetch or duplicate.
    /// Ungated by design (empty guard): a populate has nothing to refuse; it either finds new data or does not.
    /// </summary>
    IStateMachineBuilder AutoRefreshOnOpen(string name, string label, Func<TransitionContext, Task> handler);

    /// <summary>
    /// The same hook, declaring whether a PROTOCOL read — a WebDAV <c>PROPFIND</c>, an IMAP <c>SELECT</c> —
    /// may also invoke it (ABI 0.27, core issue #1286).
    /// </summary>
    /// <remarks>
    /// A SEPARATE OVERLOAD, not a new parameter on the one above, and that is not style: adding a parameter to
    /// an existing interface member is binary-breaking, so a module compiled against an older ABI would fail
    /// with <c>MissingMethodException</c> at declaration time — the shape that took the kiosk down for 1h34m
    /// (#1147, ADR 0789's rule). An overload is additive; the old signature keeps meaning
    /// <see cref="ProtocolReadRefresh.Never"/>, which is what it has always meant in practice.
    ///
    /// Declaring <see cref="ProtocolReadRefresh.WhenTenantEnables"/> does NOT switch anything on. It makes the
    /// host offer the tenant administrator a per-tenant toggle for this machine; until that is enabled, a
    /// protocol read behaves exactly as before. See <see cref="ProtocolReadRefresh"/> for why the answer is
    /// split between the module author and the tenant.
    /// </remarks>
    IStateMachineBuilder AutoRefreshOnOpen(
        string name, string label, Func<TransitionContext, Task> handler, ProtocolReadRefresh protocolRead);

    /// <summary>
    /// The same hook, additionally declaring the LEAST TIME between two upstream fetches (ABI 0.28, core
    /// issue #1307) — the rate limit for a hook whose collection holds DURABLE items.
    /// </summary>
    /// <remarks>
    /// The host's built-in cooldown is the staged content's own <c>ExpiresAt</c> (core ADR 0810): while an
    /// unexpired child exists, no read runs the hook. A collection of durable items — a calendar of real
    /// entries, not staged weather — never has one, so without this declaration every poll of a mounted
    /// calendar would reach the module's upstream source: the unattended-scraper shape core ADR 0756
    /// rejected. The host records each ATTEMPT (success or failure — the outbound request is the thing
    /// being limited) and skips the hook, on every surface, until <paramref name="minimumRefreshInterval"/>
    /// has passed. Declared by the MODULE because only the module knows its source's politeness terms;
    /// a third overload rather than a parameter, for the same binary-compatibility reason as the second.
    /// </remarks>
    IStateMachineBuilder AutoRefreshOnOpen(
        string name, string label, Func<TransitionContext, Task> handler, ProtocolReadRefresh protocolRead,
        TimeSpan minimumRefreshInterval);

    /// <summary>
    /// A proposal query (ABI 0.11, ADRs 0736/0769): the machine answers "who/what could fill this field?"
    /// for its subject — the epic's signature feature ("propose instructors who hold a valid Examiner
    /// Certificate"). The handler runs UNDER THE MODULE PRINCIPAL, because a proposal must read what the
    /// asking caller cannot (other pilots' dossiers) — and what it returns IS the filtering: only the
    /// items' value/label/detail leave the server, never the documents they were derived from. The host
    /// advertises it on the subject as a labeled GET rel, gated on the right to edit index data (it
    /// exists to fill <paramref name="fillsFieldName"/>), and both clients surface it as a picker beside
    /// that field's editor. Read-only by contract: no engine transaction, and a handler that writes is a
    /// bug (use a transition for acts).
    /// </summary>
    IStateMachineBuilder Proposal(string name, string label, string fillsFieldName, Func<TransitionContext, Task<IReadOnlyList<ProposalItem>>> handler);
}

/// <summary>One proposable answer (ABI 0.11): <paramref name="Value"/> is what the picker writes into the
/// field, <paramref name="Label"/> who or what it is, <paramref name="Detail"/> why it qualifies — already
/// filtered to what the answer needs (ADR 0736's rule).</summary>
public sealed record ProposalItem(string Value, string Label, string? Detail = null);

/// <summary>One reminder the sweep should deliver (ABI 0.5): a recipient e-mail the core resolves to a tenant
/// user, and the already-localized title and body the module composed. The module returns these from its
/// escalation handler; the core owns only delivery and the e-mail→user resolution.</summary>
public sealed record EscalationNotice(string RecipientEmail, string Title, string Message);

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

    /// <summary>The value parses as a date STRICTLY BEFORE the evaluation instant — "expired/past". The clean
    /// inverse of <see cref="DateNotPast"/>, so a Lapsed status can be a declared condition rather than a
    /// handler-evaluated afterthought. Takes no operand. Appended (ABI 0.4, flight-school #3), per this enum's
    /// append-only rule.</summary>
    DatePast,

    /// <summary>The value parses as a date in the window [now, now + <c>Operand</c> days] — "expiring soon, not
    /// yet past". <c>Operand</c> is the integer day count N. The proactive counterpart to
    /// <see cref="DateNotPast"/>: that one holds across the whole future, this narrows it to the approaching
    /// edge, so a status can warn BEFORE a deadline rather than only after it. Appended (ABI 0.4,
    /// flight-school #3).</summary>
    DateWithinDays,
}

/// <summary>A status evaluation's answer: the verdict plus every failed condition's diagnosis.</summary>
public sealed record StatusResult(bool Satisfied, IReadOnlyList<ConditionExplanation> Failed);

/// <summary>One failed condition, explained (ADR 0742): the code, the evaluated value, the sentence.</summary>
public sealed record ConditionExplanation(string Code, string? Value, string Text);
