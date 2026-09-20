namespace SimplArchive.ModuleAbi;

/// <summary>
/// The ABI's own version (ADR 0741: major locks, minor floats). A module declares what it was built against
/// (<see cref="IIndustryModule.AbiMajorVersion"/>, <see cref="IIndustryModule.AbiMinorVersion"/>); the host
/// refuses a mismatch cleanly — an admin-facing message, the module inactive, the tenant's data untouched.
/// </summary>
public static class ModuleAbiVersion
{
    /// <summary>0 while the mechanism is being proven — semver's own convention that everything may
    /// change. Becomes 1 when the first module ships commercially, and changes thereafter only as a
    /// deliberate, rare, breaking act.</summary>
    public const int Major = 0;

    /// <summary>
    /// The ABI's minor version — raised whenever anything is ADDED to this assembly's surface (#1147).
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Minor floats" means a module built against an OLDER minor keeps working, and that is now true rather
    /// than assumed: additions go in as init-only properties or secondary constructors, never as new
    /// primary-constructor parameters (see <see cref="ModuleMaskSeed"/>). A module built against a NEWER
    /// minor than the host is the case that genuinely cannot work — it may call members this host does
    /// not have — and the host's module loader refuses it.
    /// </para>
    /// <para>
    /// This exists because the promise had no enforcement and turned out to be false. ABI 0.21 added a
    /// parameter to <c>ModuleMaskSeed</c>'s primary constructor, which changed its signature; a module built
    /// against 0.20 loaded cleanly, then threw <c>MissingMethodException</c> from a static initializer and
    /// took the whole host down with it. Nothing in the version gate could see it, because the gate compared
    /// only the major and the major had not moved.
    /// </para>
    /// <para>
    /// 0.22 exists to UNDO that: the parameter became an init-only property, so a 0.20 module runs on this
    /// host again. Note that the gate below could never have fixed it — the broken case was an OLDER module
    /// on a NEWER host, which "minor floats" is supposed to allow. The gate stops the opposite direction;
    /// only an additive ABI makes the promise itself true. 0.21 is best treated as withdrawn.
    /// </para>
    /// </remarks>
    public const int Minor = 27;
}
