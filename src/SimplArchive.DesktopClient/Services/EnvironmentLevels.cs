using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The environments a server profile can declare itself to be — a fixed set, not free text (#501).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why fixed:</b> this is a support affordance, not a branding one. An administrator with three
/// near-identical windows open needs the one glance-signal to be the same on every machine and in every
/// language — a free-text field would give one laptop "Prod" and another "production!", which is exactly the
/// ambiguity the banner exists to remove. A deployment whose taxonomy doesn't fit simply picks nothing.
/// </para>
/// <para>
/// <b>Why these colours:</b> each was checked for WCAG AA contrast with white text (development 4.6:1,
/// integration 5.0:1, production 6.4:1). Production gets to be <i>red</i> here precisely because the banner is
/// not the accent: on the accent, red puts a red Save beside a red Delete, which is why the production
/// <i>theme</i> had to settle for burnt orange (ADR 0578). The banner carries no actions, so red is safe — and
/// unmistakable, which is the point.
/// </para>
/// <para>
/// <b>The empty string is a first-class value:</b> it means "no banner", it is the default, and it is what a
/// single-deployment customer stays on forever. An <i>unknown</i> stored value also resolves to no banner
/// rather than an error — same posture as a missing theme (ADR 0578): a support cue is never worth blocking a
/// login over.
/// </para>
/// </remarks>
public static class EnvironmentLevels
{
    /// <summary>One declarable environment: a stable id, a localized display name, and the banner colour.</summary>
    public sealed record Level(string Id, string Name, string Color);

    /// <summary>The pickable set, "(none)" first — what the server manager's drop-down shows.</summary>
    public static IReadOnlyList<Level> All =>
    [
        new(string.Empty, Strings.Get("EnvNone"), string.Empty),
        new("development", Strings.Get("EnvDevelopment"), "#15803D"),
        new("integration", Strings.Get("EnvIntegration"), "#B45309"),
        new("production", Strings.Get("EnvProduction"), "#B91C1C"),
    ];

    /// <summary>The banner for a stored id — or null for empty/unknown, which both mean "no banner".</summary>
    public static Level? Resolve(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(l => l.Id.Length > 0 && string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));
}
