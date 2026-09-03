using SimplArchive.Domain.Modules;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// The one place the escalate → grace → self-deactivate arithmetic lives (ADR 0740). Active is DERIVED at
/// every ask from the stored contract end date — no flag, no sweep, no event to miss: the module "turns
/// off" the instant the math says so, and a renewal filed during grace turns it back on the same way.
/// </summary>
public static class ModuleActivationPolicy
{
    /// <summary>
    /// How long the behaviour keeps running past the support contract's end (ADR 0740: a flight school
    /// mid-season does not lose its state machines overnight over a late invoice). A core constant, the
    /// same for every module — deliberately not configuration, which would be a lever that guts the
    /// enforcement story.
    /// </summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(30);

    /// <summary>The first instant the contract no longer covers — the end DAY is inclusive
    /// (<see cref="ModuleActivation.SupportContractEndDate"/> stores its midnight UTC).</summary>
    public static DateTimeOffset ExpiresAt(ModuleActivation activation) =>
        activation.SupportContractEndDate.AddDays(1);

    /// <summary>The instant the grace runs out and the module deactivates itself.</summary>
    public static DateTimeOffset DeactivatesAt(ModuleActivation activation) =>
        ExpiresAt(activation) + GracePeriod;

    /// <summary>Whether the module's behaviour is on for this tenant right now.</summary>
    public static bool IsActive(ModuleActivation activation, DateTimeOffset now) =>
        now < DeactivatesAt(activation);

    /// <summary>Whether the contract has ended but the grace period still carries the behaviour —
    /// the window in which escalation says "renew now" rather than "renew soon".</summary>
    public static bool IsInGrace(ModuleActivation activation, DateTimeOffset now) =>
        now >= ExpiresAt(activation) && now < DeactivatesAt(activation);
}
