using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Booking;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Shows every active module a booking before the core saves it, and lets it refuse (ADR 0781).
/// </summary>
/// <remarks>
/// <para>
/// The seam exists because ADR 0780 stores offered time without deciding what it authorises: an instructor's
/// published availability standing as consent needs to tell a student from an instructor, which is the
/// module's knowledge, not the core's.
/// </para>
/// <para>
/// <b>A module that cannot be consulted refuses the booking</b> (owner decision, and the reason is written
/// out in <see cref="IIndustryModule.ReviewBooking"/>): admitting the unvetted fails silently, and a consent
/// rule that has quietly stopped applying is discovered by a double-booking rather than by a screen.
/// </para>
/// </remarks>
public sealed class ModuleBookingAdmissionReviewer : IBookingAdmissionReviewer
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IServiceProvider _services;
    private readonly ILogger<ModuleBookingAdmissionReviewer> _logger;

    public ModuleBookingAdmissionReviewer(
        SimplArchiveDbContext dbContext, IServiceProvider services, ILogger<ModuleBookingAdmissionReviewer> logger)
    {
        _dbContext = dbContext;
        _services = services;
        _logger = logger;
    }

    public async Task ReviewAsync(BookingAdmissionFacts facts, CancellationToken cancellationToken = default)
    {
        var modules = (_services.GetService(typeof(IReadOnlyList<ModuleLoader.LoadedModule>))
            as IReadOnlyList<ModuleLoader.LoadedModule> ?? [])
            .Where(m => m.Module.ReviewBooking is not null)
            .ToList();
        if (modules.Count == 0)
        {
            return; // the overwhelmingly common case — no module vets bookings, so nothing is asked
        }

        var request = new BookingAdmissionRequest(
            facts.BookingDocumentId,
            facts.StartsAtUtc,
            facts.EndsAtUtc,
            [.. facts.Claims.Select(c => new BookingAdmissionClaim(c.ResourceDocumentId, c.MaskId, c.IsHolding, c.RepresentsUserId))],
            facts.IsNew,
            facts.WriterUserId,
            facts.WriterServiceAccountId);

        var now = DateTimeOffset.UtcNow;
        foreach (var loaded in modules)
        {
            if (!await ModuleActivationCheck.IsActiveAsync(_dbContext, loaded.Module.ModuleId, now, cancellationToken))
            {
                continue;
            }

            // The facade is resolved per review rather than injected, for the same reason the finalizer is
            // (ADR 0781): this service sits on a path the facade can re-enter, and a constructor edge would
            // be a cycle the container resolves at startup rather than a re-entrancy the runtime handles.
            var archive = (IModuleArchiveFacade)_services.GetService(typeof(IModuleArchiveFacade))!;

            try
            {
                await loaded.Module.ReviewBooking!(new BookingAdmissionContext(request, archive, _services));
            }
            catch (ModuleApiException)
            {
                // The module's OWN refusal: deliberate, carrying its code and localized args. Rethrow
                // untouched — the Api handler renders it as the same RFC 7807 problem a core refusal gets,
                // and wrapping it here would replace the module's specific reason ("no published availability
                // covering 14:00") with a generic one, which is exactly the collapse ADR 0626 warns about.
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // the caller went away; not this module's failure to answer
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Module {ModuleId} failed to vet booking {BookingDocumentId}; the booking is refused. "
                    + "Trace carries the exchange (ADR 0626).", loaded.Module.ModuleId, facts.BookingDocumentId);
                throw new BookingVettingFailedException(loaded.Module.ModuleId, ex);
            }
        }
    }
}
