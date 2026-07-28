namespace SimplArchive.Domain.Documents;

// One selectable OCR language: its Tesseract code (the traineddata basename) and a display name. See ADR
// "Per-tenant / per-version OCR languages".
public sealed record OcrLanguageOption(string Code, string DisplayName);

// The static catalog of OCR languages the system supports (ADR "Per-tenant / per-version OCR languages") —
// the fixed set a future mask field offers as a multi-select. A selection is stored as Tesseract's native
// "+"-joined string of these codes (e.g. "eng+spa+por"), on Tenant.DefaultOcrLanguages and, optionally,
// DocumentVersion.OcrLanguages. Every code here must have matching tessdata installed in the `ocr` sidecar
// (ocr/Dockerfile) — the two lists are kept in sync by hand.
public static class OcrLanguages
{
    // The system-wide default, and the seed value for a new Tenant's DefaultOcrLanguages — the official Swiss
    // languages plus English.
    public const string Default = "eng+deu+fra+ita";

    public static readonly IReadOnlyList<OcrLanguageOption> Supported =
    [
        new("eng", "English"),
        new("enm", "English, Middle (1100–1500)"),
        new("deu", "German"),
        new("fra", "French"),
        new("ita", "Italian"),
        new("spa", "Spanish"),
        new("spa_old", "Spanish, Castilian – Old"),
        new("por", "Portuguese"),
        new("ron", "Romanian"),
        new("rus", "Russian"),
    ];
}
