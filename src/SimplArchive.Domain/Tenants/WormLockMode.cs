namespace SimplArchive.Domain.Tenants;

// The S3 Object Lock retention mode used when a document version's blob becomes immutable (WORM) under a
// retention policy (ADR "WORM / immutable document versions (S3 Object Lock)"). Governs *retention* locks only;
// legal-hold locks are S3's mode-less on/off "legal hold" and are always absolute until released.
public enum WormLockMode
{
    // A holder of the bypass permission can shorten/remove a retention lock. Safer for dev/showcase; still
    // demonstrates real storage-enforced WORM. The default.
    Governance = 0,

    // Absolute — nobody, not even root, can delete/shorten before the retain-until date. True compliance WORM.
    Compliance = 1,
}
