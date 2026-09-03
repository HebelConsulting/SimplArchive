namespace SimplArchive.ModuleAbi;

/// <summary>
/// The ABI's own major version (ADR 0741: major locks, minor floats). A module declares the major it was
/// built against (<see cref="IIndustryModule.AbiMajorVersion"/>); the host refuses a mismatch cleanly —
/// an admin-facing message, the module inactive, the tenant's data untouched.
/// </summary>
public static class ModuleAbiVersion
{
    /// <summary>0 while the mechanism is being proven — semver's own convention that everything may
    /// change. Becomes 1 when the first module ships commercially, and changes thereafter only as a
    /// deliberate, rare, breaking act.</summary>
    public const int Major = 0;
}
