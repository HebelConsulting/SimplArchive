using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The bytes an intray overwrite set aside, and the way back to them (#799).
/// </summary>
/// <remarks>
/// <para>
/// <b>Preservation without retrieval is half a feature.</b> Since #794 every write that can replace an intray
/// item copies the outgoing bytes to <c>inbox-previous/</c> first — a net added because a word processor saved
/// correctly and then, six seconds later, rolled its own save back by moving an EMPTY backup over the file,
/// and 13 KB of somebody's work ceased to exist. The content has been safe ever since and completely
/// unreachable: no endpoint read it, so recovering anything meant going into object storage by hand.
/// </para>
/// <para>
/// <b>A sibling controller</b> rather than three more actions on <c>IntrayController</c>, which is already 888
/// lines — the recipe ADR 0571 records, applied before the limit rather than after it.
/// </para>
/// <para>
/// <b>Reached by a REL from the intray resource</b>, and therefore available to every client including
/// <c>saconsole</c> — which is a client (ADR 0543; its own csproj says so), so an endpoint it reaches by
/// following a rel is complete, not a gap waiting for a user interface. The clients may grow a button later;
/// the recovery path does not depend on that.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/intray/previous")]
[Authorize]
public class IntrayPreviousController(
    IObjectStorageClient storage,
    ICurrentUserAccessor currentUser,
    ICurrentTenantAccessor currentTenant,
    IAuditRecorder audit) : ControllerBase
{
    public class PreservedItemResource : HypermediaResource
    {
        public string Name { get; set; } = string.Empty;

        public long Size { get; set; }

        /// <summary>When the copy was set aside — which is when the overwrite happened.</summary>
        public DateTimeOffset PreservedAt { get; set; }

        /// <summary>When it expires, so a caller can see how long is left rather than compute it.</summary>
        public DateTimeOffset ExpiresAt { get; set; }
    }

    public class PreservedItemsResource : HypermediaResource
    {
        public List<PreservedItemResource> Items { get; set; } = [];
    }

    /// <summary>What is recoverable for the signed-in user.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (Scope() is not var (tenantId, userId))
        {
            return Forbid();
        }

        var prefix = ObjectKeyPrefixes.UserInboxPrevious(tenantId, userId);
        var objects = await storage.ListObjectsAsync(prefix, cancellationToken);

        var items = objects
            .Select(o => new PreservedItemResource
            {
                Name = o.Key[prefix.Length..],
                Size = o.Size,
                PreservedAt = o.LastModified,
                ExpiresAt = o.LastModified + Infrastructure.Intray.PreservedBytesSweepWorker.Lifetime,
                Links =
                [
                    new Link("restore", $"/api/intray/previous/{Uri.EscapeDataString(o.Key[prefix.Length..])}/restore", "POST"),
                ],
            })
            .OrderByDescending(i => i.PreservedAt)
            .ToList();

        return Ok(new PreservedItemsResource
        {
            Items = items,
            Links = [new Link("self", "/api/intray/previous", "GET")],
        });
    }

    // Standing convention: every GET action gets a companion HEAD action of its own.
    [HttpHead]
    public IActionResult ListHead() => Scope() is null ? Forbid() : NoContent();

    /// <summary>Puts a preserved copy back into the intray under its own name.</summary>
    /// <remarks>
    /// <b>The restore is itself reversible</b>, because it goes through the same preservation the net is made
    /// of: whatever is in the intray right now is copied aside before being replaced. A recovery that could
    /// destroy the thing it was asked to replace would be a worse trap than the one it fixes — somebody
    /// restoring the wrong item would have no second chance, and this surface exists precisely for people who
    /// have already lost something once.
    /// </remarks>
    [HttpPost("{name}/restore")]
    public async Task<IActionResult> Restore(string name, CancellationToken cancellationToken)
    {
        if (Scope() is not var (tenantId, userId))
        {
            return Forbid();
        }

        var preserved = ObjectKeyPrefixes.UserInboxPrevious(tenantId, userId) + name;
        if (!await storage.ExistsAsync(preserved, cancellationToken))
        {
            return NotFound();
        }

        var target = ObjectKeyPrefixes.UserInbox(tenantId, userId) + name;

        // Set the CURRENT bytes aside first, so this is reversible — and only when there is something to lose,
        // the same rule the net itself applies (an empty file is not worth a copy, and keeping one would mean
        // a restore could overwrite the very copy it is restoring from).
        if (await storage.ExistsAsync(target, cancellationToken)
            && await storage.GetObjectSizeAsync(target, cancellationToken) > 0)
        {
            await storage.CopyObjectAsync(target, preserved + ".replaced", cancellationToken);
        }

        await storage.CopyObjectAsync(preserved, target, cancellationToken);

        // AFTER the copy, so the trail records what happened rather than what was attempted — and recorded at
        // all because this is the one intray act that deliberately overwrites live bytes. "Where did this file
        // come from" should be answerable when the answer is "somebody recovered it".
        await audit.RecordAsync(
            AuditActions.IntrayItemRestored, "IntrayItem", userId, name,
            "Restored the bytes a previous overwrite set aside", cancellationToken: cancellationToken);

        return NoContent();
    }

    private (Guid TenantId, Guid UserId)? Scope() =>
        currentTenant.TenantId is { } tenantId && currentUser.UserId is { } userId ? (tenantId, userId) : null;
}
