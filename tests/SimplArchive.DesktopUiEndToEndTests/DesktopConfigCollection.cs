namespace SimplArchive.UiEndToEndTests;

// Serialises the pure-VM config tests (logon + server manager) — they share the static
// ServerProfileStore.PathOverride, so they must not run in parallel with each other.
[CollectionDefinition("DesktopConfig")]
public class DesktopConfigCollection
{
}
