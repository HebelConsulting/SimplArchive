namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// Where a New Contact / New Appointment will be filed: a collection the server said the caller may create in,
/// and the address to create at (#631).
/// </summary>
/// <remarks>
/// <para>
/// One type for both families rather than one per editor: they differ in nothing but the rel the href came
/// from, and a second copy is the kind that gets a fix the first never sees.
/// </para>
/// <para>
/// The href is always an advertised one — <c>contacts</c> or <c>appointments</c> off the collection's own
/// listing — so the dialog never composes an API URL and never offers a target the server would refuse: a
/// collection that does not advertise the rel simply produces no target (ADR 0543).
/// </para>
/// </remarks>
/// <param name="DisplayName">Parent-qualified, so two same-named collections are tellable apart (ADR 0619).</param>
/// <param name="CreateHref">The collection's advertised create address.</param>
public sealed record CreateTarget(string DisplayName, string CreateHref)
{
    /// <summary>What the picker shows. Overridden so the ComboBox needs no item template.</summary>
    public override string ToString() => DisplayName;
}
