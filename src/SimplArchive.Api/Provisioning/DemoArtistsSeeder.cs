using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Provisioning;

/// <summary>
/// The Events department of the demo archive (#659): an artist-booking tree, with each act's contact card and
/// its real concert schedule.
/// </summary>
/// <remarks>
/// <para>
/// The rest of the seed is deliberately fictional. This part is <b>real, published data</b> — three touring
/// acts, their own websites, and the dates those sites advertise — because the Contacts and Calendar tabs only
/// become legible with content that behaves like the real thing: an act with 31 dates across five countries, an
/// act with 28 across one, and one with 8. A calendar holding three invented entries demonstrates the widget;
/// this demonstrates the feature.
/// </para>
/// <para>
/// <b>Only what the artists publish is stored.</b> Two of the three publish no e-mail, phone or postal address
/// at all — so their cards carry a name, an organisation and a website, and nothing is invented to fill the
/// gaps. A card that looks sparse is the honest rendering of a site that says little; a fabricated phone number
/// attached to a real named person is worse than an empty field. (One number on a site was a VENUE's booking
/// line, not the artist's, and is deliberately absent for the same reason.)
/// </para>
/// <para>
/// Composed through <see cref="IContactCardComposer"/> and <see cref="IAppointmentComposer"/> — the product's
/// own writers — rather than hand-written vCard/iCalendar text. That is what makes the seeded items byte-for-byte
/// what the app itself produces, escaping and line folding included, so the demo cannot drift from the format
/// the editors round-trip.
/// </para>
/// </remarks>
internal static class DemoArtistsSeeder
{
    /// <summary>One concert: a local wall-clock start, what is playing, and where.</summary>
    /// <param name="When"><c>yyyy-MM-dd</c> for an all-day entry, or <c>yyyy-MM-dd HH:mm</c> for a timed one.</param>
    private sealed record Concert(string When, string Title, string Place);

    /// <summary>An act: its folder name, the card it gets, and the dates its site advertises.</summary>
    /// <param name="Colour">
    /// The calendar's own colour (ADR 0620). Distinct per act, because the Calendar tab OVERLAYS the ticked
    /// collections into one list and the swatch is the only thing saying which act a row belongs to — three
    /// calendars sharing a colour makes the merged view unreadable, which is the state the feature exists to
    /// avoid. Blue / amber / purple rather than the obvious red-green pair, so the three stay tellable apart
    /// for a red-green colour-blind reader too.
    /// </param>
    private sealed record Artist(
        string Name, string? Organization, string? Email, string Url, string Colour, IReadOnlyList<Concert> Concerts);

    // Dates as published on each act's own site (fetched 2026-08-19). Coloma's are announced without a time, so
    // they seed as ALL-DAY entries rather than being given an invented hour — which is also the only thing in
    // this seed that exercises the all-day path.
    private static readonly IReadOnlyList<Concert> ColomaConcerts =
    [
        new("2026-08-21", "Lluís Coloma Trio", "La Guitarra, Palafrugell, Spain"),
        new("2026-08-29", "Lluís Coloma Trio", "Sant Antoni de Calonge, Spain"),
        new("2026-09-15", "Lluís Coloma & Héctor Martín", "Ideal Cocktail Bar, Spain"),
        new("2026-09-17", "Lluís Coloma Trio", "700 Milles BCN, Barcelona, Spain"),
        new("2026-09-19", "Lluís Coloma Trio & Kid Carlos", "Festival Blues Tomares, Spain"),
        new("2026-09-25", "Lluís Coloma — Piano Solo", "Las Águilas, Murcia, Spain"),
        new("2026-09-27", "Lluís Coloma Trío", "Cafe Central, Madrid, Spain"),
        new("2026-10-02", "Lluís Coloma Trio", "Casino de Granollers, Spain"),
        new("2026-10-03", "Lluís Coloma Trio", "Altafulla, Spain"),
        new("2026-10-15", "Lluís Coloma — Piano Solo", "Blagnac, France"),
        new("2026-10-16", "Lluís Coloma — Piano Solo", "Fresselines, France"),
        new("2026-10-17", "Lluís Coloma — Piano Solo", "Beuvron-en-Auge, France"),
        new("2026-10-18", "Lluís Coloma & Renaud Patigny", "Brussels, Belgium"),
        new("2026-10-20", "Lluís Coloma & Renaud Patigny", "Lasne, Belgium"),
        new("2026-10-21", "Lluís Coloma — Piano Solo", "Dendermonde, Belgium"),
        new("2026-10-27", "Lluís Coloma & Héctor Martín", "Ideal Cocktail Bar, Spain"),
        new("2026-11-08", "San Francisco Boogie Woogie Fest — USA Tour", "San Francisco Jazz Center, USA"),
        new("2026-11-14", "Blues & Boogie Piano Summit — USA Tour", "Cincinnati, USA"),
        new("2026-11-20", "Lluís Coloma & Blue Lou Marini", "Cava Urpí, Sabadell, Spain"),
        new("2026-11-22", "Lluís Coloma & Blue Lou Marini", "Sala Jamboree, Spain"),
        new("2026-11-24", "Lluís Coloma & Héctor Martín", "Ideal Cocktail Bar, Spain"),
        new("2026-11-25", "Lluís Coloma & Kid Carlos", "Patanegra Club, Madrid, Spain"),
        new("2026-11-26", "Lluís Coloma & Kid Carlos", "Patanegra Club, Madrid, Spain"),
        new("2026-11-28", "Lluís Coloma Trio & James Goodwin & Fabrice Eulry", "Boogie Woogie Jubilee, France"),
        new("2026-11-29", "Lluís Coloma Trío", "Sant Cugat del Vallès, Spain"),
        new("2026-12-17", "Lluís Coloma Trio", "700 Milles BCN, Barcelona, Spain"),
        new("2026-12-27", "Lluís Coloma — Piano Solo", "Kammgarn, Kaiserslautern, Germany"),
        new("2027-01-05", "XXIII Blues & Boogie Reunion 2027", "Nova Jazz Cava, Terrassa, Spain"),
        new("2027-01-06", "XXIII Blues & Boogie Reunion 2027", "Nova Jazz Cava, Terrassa, Spain"),
        new("2027-01-09", "Lluís Coloma & Frank Muschalle", "Kirchheim, Germany"),
        new("2027-01-28", "Lluís Coloma Trio", "IES Poeta Maragall, Barcelona, Spain"),
    ];

    private static readonly IReadOnlyList<Concert> ZinggConcerts =
    [
        new("2026-08-22 19:00", "Solo", "Hotel Europa Suites, St. Moritz, Switzerland"),
        new("2026-08-23 10:00", "Boogie Woogie Brunch", "Restaurant Promulins, Samedan, Switzerland"),
        new("2026-08-26 20:00", "Duo", "Parco di Orselina, Orselina, Switzerland"),
        new("2026-11-22 10:30", "Trio — Musikmatinée", "Kulturforum Laufen, Laufen, Switzerland"),
        new("2026-11-23 20:00", "Solo", "Maison Hornberg, Saanenmöser, Switzerland"),
        new("2026-12-01 20:00", "Trio and Guests", "Theater Fauteuil, Kaisersaal, Basel, Switzerland"),
        new("2026-12-22 20:00", "Trio — Boogie Woogie Xmas", "Resort Hof Weissbad, Weissbad, Switzerland"),
        new("2027-01-02 20:00", "Trio — Private Party", "Solothurn, Switzerland"),
    ];

    private static readonly IReadOnlyList<Concert> TubaSkinnyConcerts =
    [
        new("2026-08-27 18:30", "Mace Chasm Farm", "Keeseville, NY, United States"),
        new("2026-08-28 12:00", "Summer Hoot 2026", "Olivebridge, NY, United States"),
        new("2026-08-29 19:00", "The Iron Horse", "Northampton, MA, United States"),
        new("2026-08-30 19:00", "Nashua Center for the Arts", "Nashua, NH, United States"),
        new("2026-09-01 17:00", "Shalin Liu Performance Center", "Rockport, MA, United States"),
        new("2026-09-01 20:00", "Shalin Liu Performance Center", "Rockport, MA, United States"),
        new("2026-09-02 19:00", "Payomet Performing Arts Center", "North Truro, MA, United States"),
        new("2026-09-03 20:00", "Narrows Center For The Arts", "Fall River, MA, United States"),
        new("2026-09-04 20:00", "StageOne at FTC", "Fairfield, CT, United States"),
        new("2026-09-05 14:00", "Delaware Valley Bluegrass Festival 2026", "Woodstown, NJ, United States"),
        new("2026-09-06 20:00", "Rams Head On Stage", "Annapolis, MD, United States"),
        new("2026-09-07 20:00", "Rams Head On Stage", "Annapolis, MD, United States"),
        new("2026-09-10 19:30", "Kenan Auditorium", "Wilmington, NC, United States"),
        new("2026-09-11 19:00", "Cain Center For The Arts", "Cornelius, NC, United States"),
        new("2026-09-12 20:30", "Radish Fest", "Asheville, NC, United States"),
        new("2026-09-13 18:00", "Newberry Opera House", "Newberry, SC, United States"),
        new("2026-09-19 18:00", "dba New Orleans", "New Orleans, LA, United States"),
        new("2026-09-23 19:30", "Good Shepherd Episcopal Lake Charles LA", "Lake Charles, LA, United States"),
        new("2026-09-25 20:00", "Buffa's", "New Orleans, LA, United States"),
        new("2026-09-26 18:00", "dba New Orleans", "New Orleans, LA, United States"),
        new("2026-10-15 19:30", "Appell Center for the Performing Arts", "York, PA, United States"),
        new("2026-10-16 20:00", "Harvester Performance Center", "Rocky Mount, VA, United States"),
        new("2026-10-18 16:00", "Williamsburg Presbyterian Church", "Williamsburg, VA, United States"),
        new("2026-10-24 18:00", "dba New Orleans", "New Orleans, LA, United States"),
        new("2026-10-29 18:00", "The Tigermen Den", "New Orleans, LA, United States"),
        new("2026-10-31 19:00", "Blackpot Festival 2026", "Lafayette, LA, United States"),
        new("2026-11-12 20:00", "The Freight", "Berkeley, CA, United States"),
        new("2026-11-13 20:00", "The Center For the Arts", "Grass Valley, CA, United States"),
    ];

    private static readonly IReadOnlyList<Artist> Artists =
    [
        new("Lluís Coloma", "Lluís Coloma — Blues & Boogie Woogie Piano", null, "https://lluiscoloma.com", "#1e88e5", ColomaConcerts),
        new("Silvan Zingg", null, null, "https://www.silvanzingg.com", "#f57c00", ZinggConcerts),
        new("Tuba Skinny", null, "tubaskinny@gmail.com", "https://tubaskinny.com", "#8e24aa", TubaSkinnyConcerts),
    ];

    internal static async Task SeedAsync(
        IServiceProvider services, SimplArchiveDbContext dbContext, IObjectStorageClient storage,
        Guid tenantId, Guid repositoryId, Guid adminId, DateTimeOffset now, DocumentFinalizer finalizer,
        string mailboxAddress)
    {
        var folderMask = await FolderMask.CurrentVersionIdAsync(dbContext, tenantId, WellKnownMaskIds.Folder, CancellationToken.None);
        var addressbookMask = await FolderMask.CurrentVersionIdAsync(dbContext, tenantId, WellKnownMaskIds.Addressbook, CancellationToken.None);
        var calendarMask = await FolderMask.CurrentVersionIdAsync(dbContext, tenantId, WellKnownMaskIds.Calendar, CancellationToken.None);

        // Grow-later seeds strand what is already there (#574), so every step checks rather than assuming an
        // empty tree: the kiosk resets nightly, a developer's volume does not.
        var departments = await FolderAsync(dbContext, tenantId, repositoryId, "Departments", adminId, now, folderMask);
        var events = await FolderAsync(dbContext, tenantId, departments.Id, "Events", adminId, now, folderMask);
        await FolderAsync(dbContext, tenantId, departments.Id, "Catering", adminId, now, folderMask);

        await SeasonAsync(dbContext, storage, services, tenantId, events.Id, adminId, now, finalizer, calendarMask);

        // The department's own mailbox (#703 PR 4): the worked example of what the issue introduces, so a
        // visitor can SEE a departmental mailbox rather than read that one is possible. The address arrives
        // derived from the admin's domain (#432's rule), so a local Compose stack claims
        // events@simplarchive.local while the kiosk claims events@demo.simplarchive.dev — never a hardcoded
        // domain the local stack cannot receive for.
        var mailboxMask = await FolderMask.CurrentVersionIdAsync(dbContext, tenantId, WellKnownMaskIds.Mailbox, CancellationToken.None);
        var mailbox = await FolderAsync(dbContext, tenantId, events.Id, "Mailbox", adminId, now, mailboxMask);
        await ClaimAsync(dbContext, tenantId, mailbox.Id, mailboxAddress);

        var artistsFolder = await FolderAsync(dbContext, tenantId, events.Id, "Artists", adminId, now, folderMask);
        var contactsFolder = await FolderAsync(dbContext, tenantId, artistsFolder.Id, "Contacts", adminId, now, addressbookMask);

        var contacts = services.GetRequiredService<IContactCardComposer>();
        var appointments = services.GetRequiredService<IAppointmentComposer>();

        foreach (var artist in Artists)
        {
            var artistFolder = await FolderAsync(dbContext, tenantId, artistsFolder.Id, artist.Name, adminId, now, folderMask);
            var concertsFolder = await FolderAsync(dbContext, tenantId, artistFolder.Id, "Concerts", adminId, now, calendarMask);
            await SetColourAsync(dbContext, tenantId, concertsFolder, calendarMask, artist.Colour);

            // The card lives ONCE, in the shared addressbook — a Contact's primary home must be a typed folder,
            // and its appearance beside the artist's own material is a reference (ADR 0619's containment rule
            // is why this is the only shape available, and it happens to be the right one: one card, many
            // places, and an edit in either is the same card).
            var card = new ContactCard(
                FormattedName: artist.Name, GivenName: null, FamilyName: null,
                Organization: artist.Organization, Title: null,
                Emails: artist.Email is { } mail ? [new ContactField(mail, "work")] : [],
                Phones: [], Addresses: [], Birthday: null, Url: artist.Url, Note: null);

            var contact = await ItemAsync(
                dbContext, storage, tenantId, contactsFolder.Id, artist.Name, adminId, now, finalizer,
                contacts.Merge(null, card, $"demo-artist-{Slug(artist.Name)}"), ".vcf", "text/vcard");

            if (contact is { } card2
                && !await dbContext.DocumentReferences.AnyAsync(
                    r => r.ParentFolderId == artistFolder.Id && r.TargetDocumentId == card2.Id))
            {
                dbContext.DocumentReferences.Add(new DocumentReference
                {
                    // Composed from the two deterministic ends (#781), like every id in this seeder: the seed
                    // must reproduce the SAME archive across the kiosk's nightly reseed.
                    Id = DemoId.For(tenantId, $"ref/{artistFolder.Id}/{card2.Id}"),
                    TenantId = tenantId,
                    ParentFolderId = artistFolder.Id,
                    TargetDocumentId = card2.Id,
                    CreatedByUserId = adminId,
                    CreatedAt = now,
                });
                await dbContext.SaveChangesAsync();
            }

            foreach (var concert in artist.Concerts)
            {
                var allDay = !concert.When.Contains(' ', StringComparison.Ordinal);
                var start = DateTime.ParseExact(
                    concert.When, allDay ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.None);

                // No TimeZoneId: these are the wall-clock times each site advertises, and stamping a zone we
                // merely inferred from the venue's city is how a floating time stops floating (ADR 0631).
                //
                // THE END TIME IS OURS, NOT THEIRS. None of the three sites publishes one — concerts advertise
                // a door or start time and nothing else — so the two hours below is a DISPLAY CONVENTION that
                // gives each entry visible extent in a week or month grid, not data anyone announced. It is
                // said in the item's own description as well as here, so a reader of the appointment sees it
                // too rather than mistaking it for a published set length. Everything else in this seeder is
                // "only what the subject publishes"; this is the one deliberate exception, and it is labelled
                // instead of being quietly plausible.
                // The summary, not just the document name. Classification RENAMES the document to the event's
                // SUMMARY, so a name qualified only here would be silently overwritten by the bare title — which
                // is what happened: of the two Rockport shows, the first was renamed to the plain venue and the
                // second kept the seeder's name because the rename would have collided. One name, decided once.
                var name = ConcertName(concert, artist.Concerts);
                var entry = new Appointment(
                    Summary: name,
                    Start: start,
                    End: allDay ? start : start.AddHours(2),
                    IsAllDay: allDay,
                    StartTimeZoneId: null,
                    EndTimeZoneId: null,
                    Location: concert.Place,
                    Description: allDay ? null : "End time not published — shown as a nominal two hours.",
                    RecurrenceRule: null);

                await ItemAsync(
                    dbContext, storage, tenantId, concertsFolder.Id, name, adminId, now, finalizer,
                    appointments.Merge(null, entry, $"demo-concert-{Slug(artist.Name)}-{start:yyyyMMddHHmm}"),
                    ".ics", "text/calendar");
            }
        }
    }

    /// <summary>
    /// The department's own calendar: what the Events team is staging, as opposed to what the acts play.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This one is invented, and it is in its own folder for that reason.</b> Everything under
    /// <c>Artists</c> is what the three acts publish on their own sites, and inventing a date for a real named
    /// performer would poison the one part of this seed that is true. The department's own run-of-show is the
    /// demo tenant's fiction, like the sprint planning and release cuts elsewhere, so it can carry what the
    /// real data does not happen to contain.
    /// </para>
    /// <para>
    /// What it contains that nothing else does is a <b>multi-day</b> entry. A grid that places each chip only on
    /// its start day loses everything after day one, and a seed in which every entry begins and ends inside one
    /// afternoon can never show that — so the festival week is the fixture the month grid is actually tested
    /// against. All-day rather than timed, deliberately: the all-day shape carries iCalendar's EXCLUSIVE
    /// <c>DTEND</c>, which is the subtler of the two off-by-ones and the one no other seeded entry exercises.
    /// </para>
    /// </remarks>
    private static async Task SeasonAsync(
        SimplArchiveDbContext dbContext, IObjectStorageClient storage, IServiceProvider services, Guid tenantId,
        Guid eventsFolderId, Guid adminId, DateTimeOffset now, DocumentFinalizer finalizer, Guid? calendarMask)
    {
        var season = await FolderAsync(dbContext, tenantId, eventsFolderId, "Season", adminId, now, calendarMask);

        // A fourth colour, distinct from the three acts': the Calendar tab overlays ticked collections, and the
        // swatch is the only thing saying which one a row came from (ADR 0620).
        await SetColourAsync(dbContext, tenantId, season, calendarMask, "#00897b");

        var appointments = services.GetRequiredService<IAppointmentComposer>();

        foreach (var (summary, from, days, place) in SeasonEntries)
        {
            var start = DateTime.ParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None);

            var entry = new Appointment(
                Summary: summary,
                Start: start,
                // DTEND is EXCLUSIVE for an all-day entry: a three-day run stops on the fourth day. Writing the
                // last day here instead would be the off-by-one that shortens every span by one day, and it
                // would look entirely reasonable in the file.
                End: start.AddDays(days),
                IsAllDay: true,
                StartTimeZoneId: null,
                EndTimeZoneId: null,
                Location: place,
                Description: "Illustrative: the department's own run-of-show, not a published date.",
                RecurrenceRule: null);

            await ItemAsync(
                dbContext, storage, tenantId, season.Id, summary, adminId, now, finalizer,
                appointments.Merge(null, entry, $"demo-season-{Slug(summary)}"),
                ".ics", "text/calendar");
        }
    }

    /// <summary>The department's run-of-show: a summary, its first day, how many days it runs, and where.</summary>
    private static readonly (string Summary, string From, int Days, string Place)[] SeasonEntries =
    [
        ("Festival week", "2026-08-24", 3, "Kornhausplatz, Bern, Switzerland"),
        ("Site build-up", "2026-08-21", 1, "Kornhausplatz, Bern, Switzerland"),
    ];

    /// <summary>
    /// What the concert is called: the venue, qualified only as far as it has to be to stay unique.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every name used to begin <c>yyyy-MM-dd</c>, which put an ISO date where a reader looks for a venue. It
    /// showed: a month cell is narrow, so two shows at one venue on one day read as <c>Shalin Liu P…</c> and
    /// <c>2026-09-01…</c> — the second one identifying nothing at all. A sibling-name collision was being
    /// resolved in the one place the user reads.
    /// </para>
    /// <para>
    /// The date was never needed for the reader either. A month cell already establishes the day and renders
    /// the time beside the title, so both were duplicated in every name to disambiguate the few that collide.
    /// Hence the shortest qualifier that works: the venue, then the venue and its time, then the day as well —
    /// added only for the entries that actually clash, so the common case reads as just the venue.
    /// </para>
    /// </remarks>
    private static string ConcertName(Concert concert, IReadOnlyList<Concert> all)
    {
        // Shortest first. The last is unconditionally unique — an act does not play one venue twice at the same
        // minute — so the loop always terminates on something, and uniqueness is CHECKED rather than assumed.
        Func<Concert, string>[] candidates =
        [
            c => c.Title,
            c => ConcertStart(c) is { } at && HasTime(c) ? $"{c.Title} {at:HH:mm}" : c.Title,
            c => ConcertStart(c) is { } at
                ? $"{c.Title} {at:d MMM}{(HasTime(c) ? at.ToString(" HH:mm", CultureInfo.InvariantCulture) : string.Empty)}"
                : c.Title,
        ];

        foreach (var candidate in candidates)
        {
            var name = candidate(concert);
            if (all.Count(c => candidate(c) == name) == 1)
            {
                return name;
            }
        }

        return candidates[^1](concert);
    }

    private static bool HasTime(Concert concert) => concert.When.Contains(' ', StringComparison.Ordinal);

    private static DateTime? ConcertStart(Concert concert) =>
        DateTime.TryParseExact(
            concert.When, HasTime(concert) ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            ? start
            : null;

    /// <summary>
    /// Paints the calendar with its own colour — the collection's default, which every user sees until they set
    /// a personal override (ADR 0620).
    /// </summary>
    private static async Task SetColourAsync(
        SimplArchiveDbContext dbContext, Guid tenantId, Document folder, Guid? maskVersionId, string colour)
    {
        if (maskVersionId is not { } maskVersion)
        {
            return;
        }

        var fieldId = await dbContext.FieldDefinitions
            .Where(f => f.Name == "Colour" && f.MaskVersionId == maskVersion)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync();

        if (fieldId is not { } field
            || await dbContext.FieldValues.AnyAsync(v => v.DocumentId == folder.Id && v.FieldDefinitionId == field))
        {
            return;
        }

        dbContext.FieldValues.Add(new FieldValue
        {
            Id = DemoId.For(tenantId, $"field-value/{folder.Id}/{field}"),
            TenantId = tenantId,
            DocumentId = folder.Id,
            FieldDefinitionId = field,
            Value = colour,
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>The mailbox's address claim, written only when absent (#574's rule, like every step here).</summary>
    private static async Task ClaimAsync(SimplArchiveDbContext dbContext, Guid tenantId, Guid mailboxId, string address)
    {
        var fieldId = await dbContext.MaskVersions
            .Where(v => v.MaskId == WellKnownMaskIds.Mailbox && v.IsCurrent)
            .Join(dbContext.FieldDefinitions, v => v.Id, f => f.MaskVersionId, (_, f) => f)
            .Where(f => f.Name == Infrastructure.Masks.WellKnownMaskSeeder.MailboxAddressesFieldName)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync();
        if (fieldId is not { } field || await dbContext.FieldValues.AnyAsync(v => v.DocumentId == mailboxId && v.FieldDefinitionId == field))
        {
            return;
        }

        dbContext.FieldValues.Add(new FieldValue
        {
            Id = DemoId.For(tenantId, $"field-value/{mailboxId}/{field}"),
            TenantId = tenantId,
            DocumentId = mailboxId,
            FieldDefinitionId = field,
            Value = address,
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>Get-or-create, so a restart against an existing volume neither duplicates nor throws.</summary>
    private static async Task<Document> FolderAsync(
        SimplArchiveDbContext dbContext, Guid tenantId, Guid parentId, string name, Guid adminId,
        DateTimeOffset at, Guid? maskVersionId)
    {
        if (await dbContext.Documents.FirstOrDefaultAsync(d => d.ParentId == parentId && d.Name == name) is { } existing)
        {
            return existing;
        }

        var folder = new Document
        {
            // (parent id, name) is the identity: both are stable in this seeder's data (#781).
            Id = DemoId.For(tenantId, $"folder/artists/{parentId}/{name}"),
            TenantId = tenantId,
            ParentId = parentId,
            Name = name,
            MaskVersionId = maskVersionId,
            CreatedByUserId = adminId,
            CreatedAt = at,
        };
        dbContext.Documents.Add(folder);
        await dbContext.SaveChangesAsync();
        return folder;
    }

    /// <summary>
    /// One typed item from composed text. MASKLESS on purpose: the finalizer classifies a <c>.vcf</c> as a
    /// Contact and an <c>.ics</c> as an Appointment once the bytes are there, and it is also what extracts the
    /// UID the DAV correlation depends on. Stamping a mask here would guess at what classification decides.
    /// </summary>
    private static async Task<Document?> ItemAsync(
        SimplArchiveDbContext dbContext, IObjectStorageClient storage, Guid tenantId, Guid parentId, string name,
        Guid adminId, DateTimeOffset at, DocumentFinalizer finalizer, string text, string extension, string contentType)
    {
        if (await dbContext.Documents.FirstOrDefaultAsync(d => d.ParentId == parentId && d.Name == name) is { } existing)
        {
            return existing;
        }

        var storageFolderId = DemoId.For(tenantId, $"doc/artists/{parentId}/{name}/storage");
        var versionId = DemoId.For(tenantId, $"doc/artists/{parentId}/{name}/v1");
        var document = new Document
        {
            Id = DemoId.For(tenantId, $"doc/artists/{parentId}/{name}"),
            TenantId = tenantId,
            ParentId = parentId,
            Name = name,
            CreatedByUserId = adminId,
            CreatedAt = at,
            StorageFolderId = storageFolderId,
        };
        dbContext.Documents.Add(document);
        await dbContext.SaveChangesAsync();

        var objectKey = ObjectKeyBuilder.Build(tenantId, at, storageFolderId, versionId, extension);
        using (var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)))
        {
            await storage.PutObjectAsync(objectKey, content, contentType);
        }

        // Pending + the shared finalizer, never a hand-written Confirmed version: the status is guarded by a
        // CHECK constraint, and the finalizer is what classifies and indexes it.
        var version = new DocumentVersion
        {
            Id = versionId,
            TenantId = tenantId,
            DocumentId = document.Id,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = adminId,
            CreatedAt = at,
            DocumentDate = DateOnly.FromDateTime(at.UtcDateTime),
        };
        dbContext.DocumentVersions.Add(version);
        await dbContext.SaveChangesAsync();
        await finalizer.FinalizeAsync(version, CancellationToken.None);
        return document;
    }

    /// <summary>A stable, ASCII UID fragment — the correlation key a DAV sync matches on must not shift.</summary>
    private static string Slug(string name) =>
        new(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
}
