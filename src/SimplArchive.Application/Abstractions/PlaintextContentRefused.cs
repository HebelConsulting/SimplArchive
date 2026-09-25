namespace SimplArchive.Application.Abstractions;

/// <summary>
/// A door that can only serve readable bytes was asked for a strict-tier tenant's content (#1394, ADR 0829).
/// </summary>
/// <remarks>
/// <para>
/// Thrown from the storage seam and translated at the Api boundary, the same shape a module's refusal takes —
/// Infrastructure cannot reference the Api's exception types, and the alternative, returning null for every
/// such read, would put a null-handling branch in each of a dozen doors and lose the reason with it.
/// </para>
/// <para>
/// <b>Why a refusal rather than an envelope.</b> These doors hand bytes to software that cannot decrypt CMS:
/// a mounted drive, a calendar client, a zip stream, a working copy opened in an editor. There is nothing to
/// envelope TO. Refusing them all now, and enveloping later where demand proves it, is the owner's decision
/// of 2026-09-25.
/// </para>
/// </remarks>
public sealed class PlaintextContentRefusedException(string door) : Exception(
    $"This tenant never serves document content in readable form, and {door} cannot carry an encrypted "
    + "envelope. Open the document in a client that reads through your certificate.")
{
    public string ErrorCode => "PLAINTEXT_CONTENT_REFUSED";

    /// <summary>409: the request is well formed and the rights are fine — the TIER refuses it.</summary>
    public int StatusCode => 409;

    /// <summary>The door, named so the message tells the reader which thing to stop using.</summary>
    public string Door { get; } = door;
}
