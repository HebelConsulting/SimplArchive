using Microsoft.AspNetCore.Http;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Api.Errors.Exceptions.Modules;

/// <summary>
/// A state-machine transition's guard said no (ADRs 0737/0742) — and the refusal IS the explanation.
/// </summary>
/// <remarks>
/// 409: the request was well-formed, the world said no — the same class as a slot conflict. The detail is
/// the module's own sentences with the failing values substituted (what both clients' generic action
/// surface shows the user), and the machine-readable diagnosis rides the problem's extensions
/// (<c>refusals</c>: code + value + text per failed condition), the ADR 0742 grammar on the wire — a
/// client that needs the FACT and not the sentence gets it as data (the ApiException.Extensions rule).
/// </remarks>
public sealed class MachineTransitionRefusedException : ModuleException
{
    public MachineTransitionRefusedException(string machineId, string transitionName, IReadOnlyList<ConditionExplanation> refusals)
        : base("MACHINE_TRANSITION_REFUSED", StatusCodes.Status409Conflict,
            string.Join(" ", refusals.Select(r => r.Text)),
            new Dictionary<string, object?>
            {
                ["machineId"] = machineId,
                ["transition"] = transitionName,
                ["refusals"] = refusals.Select(r => new { code = r.Code, value = r.Value, text = r.Text }).ToList(),
            })
    {
    }
}
