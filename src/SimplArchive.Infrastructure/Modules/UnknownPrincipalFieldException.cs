namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// A module's transition names a principal field its subject mask does not declare (ABI 1.6, ADR 0897). Thrown at
/// load, inside the <c>DefineStateMachines</c> seam, so the module is refused rather than shipping an act nobody
/// but an administrator can ever be offered: a misspelt field would read as "this person may sign" and behave as
/// "nobody may".
/// </summary>
public sealed class UnknownPrincipalFieldException(string machineId, string transitionName, string fieldName)
    : InvalidOperationException(
        $"Transition '{machineId}/{transitionName}' names principal field '{fieldName}', which its subject mask "
        + "does not declare. A principal field must be one of the module's own mask fields.");
