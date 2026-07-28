namespace SimplArchive.UiEndToEndTests;

// Serialises the pure-VM config tests (logon + tenant manager) — they share the static
// TenantProfileStore.PathOverride, so they must not run in parallel with each other.
[CollectionDefinition("DesktopConfig")]
public class DesktopConfigCollection
{
}
