using Microsoft.EntityFrameworkCore;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Evaluates the derived statuses a document's machines declare (ADRs 0742/0743; #1021), for the
/// document resource to carry. The same iteration the transition-link emission does — every machine whose
/// subject mask this document wears, module-active-gated (ADR 0543: an inactive module's machine does not
/// exist) — but statuses are DATA, not links, so they ride the resource body rather than the link list.
/// </summary>
/// <remarks>
/// A status is computed on every ask (ADR 0742: never stored), so this runs the machine's fact providers
/// per document GET — for <c>fs-pilot</c>, two windowed read-model queries. Acceptable at slice-2 scale;
/// a per-request cache is the recorded next step if a mask ever carries many fact-backed statuses.
/// </remarks>
public sealed class MachineStatusEvaluator(
    SimplArchiveDbContext dbContext, StateMachineCatalog catalog, StateMachineEngine engine)
{
    public async Task<IReadOnlyList<MachineStatusResource>> EvaluateAsync(
        Guid documentId, Guid? maskId, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        if (maskId is not { } subjectMaskId)
        {
            return [];
        }

        var result = new List<MachineStatusResource>();
        foreach (var machine in catalog.Machines.Values.Where(m => m.SubjectMaskId == subjectMaskId))
        {
            if (machine.ModuleId is { } declaringModule
                && !await ModuleActivationCheck.IsActiveAsync(dbContext, declaringModule, asOf, cancellationToken))
            {
                continue; // an inactive module's statuses do not exist (ADR 0543), exactly like its transitions
            }

            foreach (var statusName in machine.Statuses.Keys)
            {
                var verdict = await engine.EvaluateStatusAsync(machine.MachineId, statusName, documentId, asOf, cancellationToken);
                result.Add(new MachineStatusResource
                {
                    MachineId = machine.MachineId,
                    Name = statusName,
                    Satisfied = verdict.Satisfied,
                    Failures = verdict.Failed
                        .Select(f => new MachineStatusFailure { Code = f.Code, Value = f.Value, Text = f.Text })
                        .ToList(),
                });
            }
        }

        return result;
    }
}

/// <summary>One derived status as the document resource carries it (#1021): the verdict plus, when unmet,
/// each failed condition's ADR-0742 diagnosis.</summary>
public sealed class MachineStatusResource
{
    public string MachineId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Satisfied { get; set; }

    public List<MachineStatusFailure> Failures { get; set; } = [];
}

/// <summary>One unmet condition, explained — the code a client branches on, the evaluated value, the sentence.</summary>
public sealed class MachineStatusFailure
{
    public string Code { get; set; } = string.Empty;

    public string? Value { get; set; }

    public string Text { get; set; } = string.Empty;
}
