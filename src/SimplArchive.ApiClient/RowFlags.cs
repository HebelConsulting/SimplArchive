using System.Text.Json;

namespace SimplArchive.ApiClient;

/// <summary>What a listing row says about itself, read the same way for every row type (Node, Reference).</summary>
public static class RowFlags
{
    /// <summary>The row's <c>isFolder</c>, from its mask (#1708), or null from a server that does not send it yet.</summary>
    public static bool? IsFolderOf(JsonElement item) =>
        item.TryGetProperty("isFolder", out var f) && f.ValueKind is JsonValueKind.True or JsonValueKind.False ? f.GetBoolean() : null;
}
