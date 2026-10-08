using SimplArchive.Api.Security;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Api.Modules;

/// <summary>
/// The wire shape of a module action's outcome carrying a value revealed once: the module's
/// <see cref="ModuleActionResult"/> as written, plus what only the core adds — the QR code of a scan address (ADR 0913),
/// so a module needs no image library and both clients show the same code.
/// </summary>
public sealed class ModuleActionResponse
{
    public string? Message { get; set; }

    public RevealedValueResponse? RevealOnce { get; set; }

    public static ModuleActionResponse From(ModuleActionResult result) => new()
    {
        Message = result.Message,
        RevealOnce = result.RevealOnce is { } revealed
            ? new RevealedValueResponse
            {
                Label = revealed.Label,
                Value = revealed.Value,
                ScanAddress = revealed.ScanAddress,
                ScanImage = string.IsNullOrWhiteSpace(revealed.ScanAddress) ? null : QrCodes.PngDataUrl(revealed.ScanAddress),
            }
            : null,
    };
}

/// <summary>A value revealed once, with its scan address and that address drawn as a PNG data URL (both optional).</summary>
public sealed class RevealedValueResponse
{
    public string Label { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string? ScanAddress { get; set; }

    public string? ScanImage { get; set; }
}
