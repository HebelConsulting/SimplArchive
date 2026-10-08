namespace SimplArchive.ModuleAbi;

/// <summary>
/// What a module action's COMMIT endpoint answers (ABI 1.8, core ADR 0899), so the clients can show its outcome:
/// a sentence for the status line, and optionally a value shown exactly ONCE (an issued credential, a push key)
/// in a dialog with Copy. Return it as the commit's JSON body; anything else is treated as a plain success.
/// </summary>
/// <param name="Message">A short, already-localized sentence for the status line, or null.</param>
public sealed record ModuleActionResult(string? Message = null)
{
    /// <summary>A value the user sees once and must copy now, because only its hash is kept. The core answers the
    /// response <c>Cache-Control: no-store</c> and never logs it; the clients keep it only while the dialog is open.</summary>
    public RevealedValue? RevealOnce { get; init; }
}

/// <summary>A value revealed once: what it is (already localized, e.g. "Pull credential for ACME AG") and the value.</summary>
public sealed record RevealedValue(string Label, string Value)
{
    /// <summary>An address a phone scans to use the value, or null (ABI 1.13, core ADR 0913) — typically the value
    /// embedded in a URL, such as an app repository's address carrying its pull credential. The core draws it as a QR
    /// code beside the value, in the same uncached response, so a module needs no image library and both clients
    /// show the same code. It is as secret as the value: the same no-store and no-log rules hold.</summary>
    public string? ScanAddress { get; init; }
}
