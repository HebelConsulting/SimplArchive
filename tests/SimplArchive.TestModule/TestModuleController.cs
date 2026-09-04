using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.ModuleAbi;

namespace SimplArchive.TestModule;

/// <summary>
/// The fixture's controller — the standing proof that a module assembly referencing ONLY the ABI can
/// serve a native endpoint (ADR 0737): hypermedia envelope, caller context, the rights seam, an
/// intent-named refusal, and the HEAD companion the core's conventions require. The host's gate (not this
/// code) answers 404 MODULE_NOT_ACTIVE for tenants without an active activation.
/// </summary>
[ApiController]
[Route("api/test-module")]
public sealed class TestModuleController : ControllerBase
{
    /// <summary>What the status endpoint reports: who is calling, as the seams answered it.</summary>
    public sealed class ModuleStatusResource : HypermediaResource
    {
        public string ModuleId { get; set; } = string.Empty;

        public Guid TenantId { get; set; }

        public Guid? UserId { get; set; }

        public Guid? ServiceAccountId { get; set; }

        public bool IsTenantAdmin { get; set; }
    }

    /// <summary>A document's rights as the module sees them — the core calculator's answer, relayed.</summary>
    public sealed class ModuleRightsResource : HypermediaResource
    {
        public Guid DocumentId { get; set; }

        public bool CanSee { get; set; }

        public bool CanEditContent { get; set; }
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(
        [FromServices] IModuleCallerContext caller, CancellationToken cancellationToken) =>
        Ok(new ModuleStatusResource
        {
            ModuleId = "test-module",
            TenantId = caller.TenantId,
            UserId = caller.UserId,
            ServiceAccountId = caller.ServiceAccountId,
            IsTenantAdmin = await caller.IsTenantAdminAsync(cancellationToken),
            Links =
            [
                new Link("self", "/api/test-module/status", "GET"),
            ],
        });

    // The core's standing convention, honoured by the module: every GET action gets its own HEAD action.
    [HttpHead("status")]
    public IActionResult HeadStatus() => NoContent();

    [HttpGet("documents/{documentId:guid}/rights")]
    public async Task<IActionResult> DocumentRights(
        Guid documentId, [FromServices] IModuleDocumentRights rights, CancellationToken cancellationToken)
    {
        var answer = await rights.GetAsync(documentId, cancellationToken);
        if (!answer.CanSee)
        {
            // The module's own refusal shape (ADR 0737): an intent-named subclass of ModuleApiException,
            // translated by the host into the same RFC 7807 problem a core refusal gets.
            throw new TestDocumentNotVisibleException(documentId);
        }

        return Ok(new ModuleRightsResource
        {
            DocumentId = documentId,
            CanSee = answer.CanSee,
            CanEditContent = answer.CanEditContent,
            Links = [new Link("self", $"/api/test-module/documents/{documentId}/rights", "GET")],
        });
    }

    [HttpHead("documents/{documentId:guid}/rights")]
    public IActionResult HeadDocumentRights() => NoContent();
}

/// <summary>The caller may not see the document — or it does not exist; the two answer alike.</summary>
public sealed class TestDocumentNotVisibleException(Guid documentId)
    : ModuleApiException("TEST_DOCUMENT_NOT_VISIBLE", StatusCodes.Status404NotFound,
        $"Document {documentId} is not visible to the caller.");
