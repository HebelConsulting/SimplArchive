using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The masks area (#443's per-area shape): the tenant's mask catalogue, a mask's fields, and assigning one.
/// </summary>
/// <remarks>
/// Extracted from <c>DocumentsClient</c> rather than added to it. That file is on the standing-debt list
/// (#466) and may only get smaller, and masks are an area in their own right — every other area got its own
/// client in #443, so this is the shape the codebase already chose rather than a new one invented for a
/// ceiling.
/// </remarks>
public sealed class MasksClient
{
    private readonly ApiCore _core;

    public MasksClient(ApiCore core) => _core = core;

    /// <summary>A tenant mask option for the mask-change dropdown.</summary>
    /// <remarks>
    /// <c>SelfHref</c> is the address the catalogue advertised for this mask — reading its fields follows that
    /// rather than rebuilding <c>/api/masks/{id}</c> from the id beside it (ADR 0543/0555).
    /// </remarks>
    public sealed record MaskOptionInfo(Guid Id, string Name, string? SelfHref = null);

    /// <summary>A mask's field definition + type, for building the type-aware editor.</summary>
    public sealed record MaskFieldInfo(Guid Id, string Name, string DataType, bool IsRequired);

    /// <summary>The masks a user may actually CHOOSE for a document.</summary>
    /// <remarks>
    /// The SERVER decides which those are (ADR 0653): a folder mask types a folder, an extension-claimed mask
    /// is assigned by the classifier on upload, and a mask whose location is constrained belongs only in its
    /// admitting folder. Both clients used to derive this — differently — and offered masks the containment
    /// invariant then refused, so the user learned about it from a failed save (#580).
    /// </remarks>
    public async Task<IReadOnlyList<MaskOptionInfo>> GetMasksAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(
            await _core.RootHrefAsync("masks", cancellationToken), cancellationToken);

        var list = new List<MaskOptionInfo>();
        if (json.TryGetProperty("masks", out var masks))
        {
            foreach (var mask in masks.EnumerateArray().Where(Assignable))
            {
                list.Add(new MaskOptionInfo(
                    mask.GetProperty("id").GetGuid(),
                    mask.GetProperty("name").GetString() ?? string.Empty,
                    ApiCore.RelHref(mask, "self")));
            }
        }

        return list;
    }

    /// <summary>A mask's field definitions (+ types), for building the type-aware editors.</summary>
    public async Task<IReadOnlyList<MaskFieldInfo>> GetMaskFieldsAsync(MaskOptionInfo mask, CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(
            mask.SelfHref ?? throw new InvalidOperationException($"The mask '{mask.Name}' advertised no 'self' rel (ADR 0543/0555)."),
            cancellationToken);

        var list = new List<MaskFieldInfo>();
        if (json.TryGetProperty("fields", out var fields))
        {
            foreach (var f in fields.EnumerateArray())
            {
                list.Add(new MaskFieldInfo(
                    f.GetProperty("id").GetGuid(),
                    f.GetProperty("name").GetString() ?? string.Empty,
                    f.TryGetProperty("dataType", out var dataType) ? dataType.GetString() ?? "Text" : "Text",
                    f.TryGetProperty("isRequired", out var required) && required.GetBoolean()));
            }
        }

        return list;
    }

    /// <summary>Assigns (or changes) a document's mask. 400 REQUIRED_FIELD_MISSING surfaces as a friendly message.</summary>
    public async Task SetMaskAsync(string maskHref, Guid maskId, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.PutAsJsonAsync(maskHref, new { maskId }, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, "Could not assign the mask", cancellationToken);
    }

    public async Task ClearMaskAsync(string maskHref, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.DeleteAsync(maskHref, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, "Could not clear the mask", cancellationToken);
    }

    /// <summary>Creates a tenant mask with no fields — freely assignable, since it types nothing in particular.</summary>
    /// <remarks>
    /// Needs CanManageMasks. A mask created this way is the ordinary case a tenant admin makes: not a folder
    /// mask, claimed by no extension, constrained to no parent — so it is exactly what "freely assignable"
    /// means (ADR 0653).
    /// </remarks>
    public async Task<MaskOptionInfo> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.PostAsJsonAsync(
            await _core.RootHrefAsync("masks", cancellationToken), new { name }, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, $"Could not create the mask '{name}'", cancellationToken);

        var created = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new MaskOptionInfo(
            created.GetProperty("id").GetGuid(),
            created.GetProperty("name").GetString() ?? name,
            ApiCore.RelHref(created, "self"));
    }

    /// <summary>Absent means assignable, so a server predating ADR 0653 fills the picker rather than emptying it.</summary>
    private static bool Assignable(JsonElement mask) =>
        !mask.TryGetProperty("isFreelyAssignable", out var value) || value.GetBoolean();
}
