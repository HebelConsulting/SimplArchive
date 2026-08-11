using System.Net.Http.Json;
using SimplArchive.Client.Models;

namespace SimplArchive.Client.Services;

/// <summary>The tenant's sensitivity labels, fetched once and shared by everything that shows them.</summary>
/// <remarks>
/// <para>
/// Two surfaces need the same list for different reasons: the Repositories detail pane offers it as a picker,
/// and the Users &amp; groups tab maps a clearance rank onto a label name. When the workbench was one page they
/// shared a field; extracting the tab (ADR 0558) would otherwise have meant a second copy of a loader — and a
/// copied loader is exactly what drifts (CLAUDE.md: the same work in several places is ONE implementation).
/// </para>
/// <para>
/// Cached because the list changes only when an admin edits it, and <see cref="Invalidate"/> exists for that:
/// the labels dialog calls it on close so the next read re-fetches. Registered scoped, which in a WebAssembly
/// host is the app's lifetime.
/// </para>
/// </remarks>
public sealed class SensitivityLabelCatalog(HttpClient http, ApiRoot apiRoot)
{
    private List<SensitivityLabel>? _labels;

    /// <summary>Whether the caller may edit the labels themselves (the server's answer, not a guess).</summary>
    public bool CanManage { get; private set; }

    /// <summary>The labels, fetching them on first use. Empty on failure — a picker with no options, not a crash.</summary>
    public async Task<IReadOnlyList<SensitivityLabel>> GetAsync()
    {
        if (_labels is not null)
        {
            return _labels;
        }

        try
        {
            var resp = await http.GetFromJsonAsync<SensitivityLabelsResponse>(await apiRoot.RequireAsync("sensitivityLabels"));
            _labels = resp?.Labels ?? [];
            CanManage = resp?.CanManage ?? false;
        }
        catch (Exception)
        {
            _labels = [];
        }

        return _labels;
    }

    /// <summary>Drops the cache so the next read re-fetches — call after the labels have been edited.</summary>
    public void Invalidate() => _labels = null;
}
