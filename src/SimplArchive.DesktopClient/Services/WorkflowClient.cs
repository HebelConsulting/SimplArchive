using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The workflow area (#443, tranche 5): the task inbox and the per-version approval workflow. Rides the shared authenticated <see cref="ApiCore"/> (#443).
/// </summary>
public sealed class WorkflowClient(ApiCore core)
{
    private readonly ApiCore _core = core;


    public sealed record WorkflowTransitionInfo(string ToStatusName, string? AssignedToName, string? PerformedByName, string? RejectionReason);



    // The approval workflow on a version (ADR "Workflow / document state model", 0009). Status is the
    // WorkflowStatus int; Links maps each valid-transition rel (submit/approve/reject/release) to its href.
    public sealed record WorkflowInfo(
        int Status, string StatusName, string? AssignedToName,
        IReadOnlyList<WorkflowTransitionInfo> History, IReadOnlyDictionary<string, string> Links);

    // A pending review task assigned to the caller (backs the Tasks tab).
    public sealed record TaskInfo(Guid DocumentId, Guid? ParentId, Guid VersionId, string DocumentName, int? VersionNumber, DateTimeOffset AssignedAt, IReadOnlyDictionary<string, string>? Links = null, DateTimeOffset? DueAt = null);

    // ---- Workflow + tasks (ADR "Workflow / document state model", 0009) -----------------------------------

    public async Task<IReadOnlyList<TaskInfo>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("tasks", cancellationToken), cancellationToken);
        var list = new List<TaskInfo>();
        if (json.TryGetProperty("tasks", out var tasks))
        {
            foreach (var t in tasks.EnumerateArray())
            {
                list.Add(new TaskInfo(
                    t.GetProperty("documentId").GetGuid(),
                    t.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetGuid() : null,
                    t.GetProperty("versionId").GetGuid(),
                    t.GetProperty("documentName").GetString() ?? "",
                    t.TryGetProperty("versionNumber", out var vn) && vn.ValueKind == JsonValueKind.Number ? vn.GetInt32() : null,
                    t.TryGetProperty("assignedAt", out var a) ? a.GetDateTimeOffset() : default,
                    ApiCore.ParseLinks(t), t.TryGetProperty("dueAt", out var du) && du.ValueKind == JsonValueKind.String ? du.GetDateTimeOffset() : null));
            }
        }

        return list;
    }

    // POSTs a workflow transition action (the href comes from WorkflowInfo.Links). Throws ApiActionException
    // with the server's Problem-Details detail on a rejected transition (409/400/403).
    public async Task PostWorkflowActionAsync(string href, object? body, CancellationToken cancellationToken = default)
    {
        using var response = body is null
            ? await _core.Http.PostAsync(href.TrimStart('/'), null, cancellationToken)
            : await _core.Http.PostAsJsonAsync(href.TrimStart('/'), body, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var detail = ApiErrorText.For(null);
            try
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                if (problem.TryGetProperty("errorCode", out var c) && c.GetString() is { Length: > 0 } code)
                {
                    detail = ApiErrorText.For(code);
                }
            }
            catch (Exception) { /* keep the generic localised message */ }

            throw new ApiActionException(detail);
        }
    }
}
