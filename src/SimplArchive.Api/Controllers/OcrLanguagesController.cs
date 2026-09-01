using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Domain.Documents;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The static catalog of OCR languages the system supports (ADR "Per-tenant / per-version OCR languages") —
/// what the OCR-languages system-field picker offers. Read-only; the same fixed list for every tenant.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/ocr-languages")]
[Authorize]
public class OcrLanguagesController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(Build());

    // Standing convention: every GET has a companion HEAD.
    [HttpHead]
    public IActionResult Head() => NoContent();

    private static OcrLanguageCatalogResource Build() => new()
    {
        Languages = OcrLanguages.Supported.Select(l => new OcrLanguageResource { Code = l.Code, DisplayName = l.DisplayName }).ToList(),
        Links = [new Link("self", "/api/ocr-languages", "GET")],
    };

    public class OcrLanguageCatalogResource : HypermediaResource
    {
        public List<OcrLanguageResource> Languages { get; set; } = [];
    }

    public class OcrLanguageResource
    {
        public string Code { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;
    }
}
