#import "template.typ": conf, shot, pair, note, accent, idx, index-page

#show: doc => conf(doc)

// ─────────────────────────────────────────────────────────────────────────────
// Title page
// ─────────────────────────────────────────────────────────────────────────────
#page(numbering: none, footer: none)[
  #set align(center + horizon)
  #block[
    #text(size: 34pt, weight: "bold", fill: accent)[SimplArchive]
    #v(0.2em)
    #text(size: 18pt)[User Manual]
    #v(1.2em)
    #text(size: 11pt, fill: gray)[
      An enterprise-grade document management system \
      — the web workbench and the desktop client
    ]
  ]

  // Imprint / copyright (issue #295). The year resolves at compile time so it stays current on each
  // regeneration; the contact is a live mailto link.
  #place(bottom + center)[
    #set text(size: 8.5pt, fill: gray)
    #align(center)[
      © #datetime.today().year() \
      Hebel Consulting GmbH \
      Schweighofplatz 7 \
      6010 Kriens (LU) \
      Switzerland \
      #link("mailto:support@simplarchive.dev")[support\@simplarchive.dev]
    ]
  ]
]

// ─────────────────────────────────────────────────────────────────────────────
#outline(title: "Contents", depth: 1, indent: auto)

// ─────────────────────────────────────────────────────────────────────────────
= Introduction

SimplArchive is a multi-tenant *document management system* (DMS): a secure archive where an organisation
files, versions, classifies, searches, and governs its documents#idx("Document management system"). It is a showcase of how a senior, AI-driven
software developer can produce a complex, enterprise-grade system in a relatively short period — every feature in
this manual is really implemented and really tested.

You reach the same archive through *two clients*, which mirror each other feature for feature:

- the *web workbench*#idx("Web client") — a browser application, nothing to install;
- the *desktop client*#idx("Desktop client") — a native Windows / macOS / Linux application that can additionally open a document in its
  real desktop program and drag documents in and out of the operating-system file manager.

#pair("screenshots/web-login.png", "screenshots/desktop-logon.png",
  [The two clients: the web sign-in (left) and the desktop logon window (right).])

== Core concepts

#note[
  *Repository*#idx("Repository") — a top-level archive; it is simply a folder with no parent. *Folder* / *document* — the tree
  inside a repository. *Version*#idx("Version") — every change to a document's file is kept as a new version; nothing is
  overwritten. *Mask (document type)*#idx("Mask (document type)")#idx("Index fields") — a template of *index fields* (metadata) attached to a document. *ACL*#idx("ACL")#idx("Rights") —
  a per-document access-control list of *rights* (see, read, edit, …) granted to users and groups.
]

== A guided tour, given by your own AI

You do not have to explore alone: SimplArchive publishes a machine-readable tour script, and any AI assistant
that can drive a browser — for example one living in your browser as an extension — can perform it *for you*,
speaking your language#idx("Guided tour")#idx("AI tour").

How to do it:

+ *Ask for the tour in your own words* — the request must come from you, because a well-behaved assistant
  does not take orders from files on the internet. For example: _"Give me the guided tour of the SimplArchive
  at `<address>/llms.txt` — interview me first, and speak German."_
+ Use an assistant that can actually drive your browser. Two setups work: a *browser-extension assistant*
  (a chat conversation whose extension sees and drives your tabs), or a *local agent on your machine* (a
  command-line assistant that drives your browser and can speak through your operating system's own voice).
  A chat that only fetches pages server-side cannot click, cannot speak, and cannot reach a `localhost`
  instance at all.
+ The assistant will first *interview you*: which areas interest you (everyday filing, capture and scanning,
  collaboration, records and compliance, administration, integration) and how deep to go — a three-minute
  overview or a hands-on walkthrough.
+ It then drives the application in front of your eyes and *narrates aloud, in your mother tongue*, at your
  pace. Ask it to linger, skip ahead, or repeat — it is your tour, not a recording.

Two practical notes. On the *public demo* the assistant keeps to a read-only tour, because other visitors share
the same instance; on *your own* installation it can also demonstrate hands-on work — uploading, filing,
indexing, sharing. And if it needs to sign in, give it the account you would use yourself; on the public
demo, the published demo sign-in from the project's README works.

= Getting started

*Signing in.* Open the web client and choose *Log in*, or start the desktop client and use its logon window; enter
your e-mail and password. Your organisation may additionally require a second factor#idx("Multi-factor authentication") (a one-time code or a
passkey). You can pick the interface *language* (English, German, Italian, Spanish) and switch between a *light*
and *dark* appearance at any time.

*If sign-in suddenly refuses everything*, you have most likely mistyped several times in a row. Repeated failed
attempts are turned away for a short while#idx("Sign-in throttling") — about a minute at first, longer if they keep coming — and the wait
clears on its own, so there is nobody to ring and nothing to reset. A passkey keeps working throughout, which is
the quickest way back in if you have one.

*The workbench.* After signing in you land on the *Repositories* workbench, laid out as: the *tree* of
repositories and folders, the *contents* list of the selected folder, the *detail* pane (index data) over the
*preview*#idx("Preview"), and — along the bottom — the *tab bar* that switches between Repositories, Intray, Search, Tasks and the
rest.

#pair("screenshots/web-repositories.png", "screenshots/desktop-workbench.png",
  [The workbench in the web client (left) and the desktop client (right): tree · contents · detail · preview, with
   the bottom tab bar.])

= Browsing & previewing documents

Expand the *tree* to a folder and its documents appear in the *contents* list, which you can sort by any column.
Selecting a document shows its *index data* in the detail pane and renders a *preview* below — PDFs, images, and
converted Office/e-mail/Markdown documents alike. Full-text search hits are highlighted directly on the preview#idx("Hit highlighting"),
and you can click any word to copy it#idx("Click-to-copy") — *shift-click* appends instead of replacing, so a
few clicks collect a phrase (an invoice number, then its date) without touching the keyboard. That is the fast
way to fill index fields on the *Intray* tab: click the values in the scan's preview, paste them into the field.
In the desktop client you can also *open* the document in its native
application#idx("Open a document") — from the row's context menu, the ribbon, a double-click, or the keyboard
shortcut *⌘O* (*Ctrl+O* on Windows and Linux). The same shortcut opens the selected item on the *Intray* tab.

The preview's toolbar carries the *zoom*#idx("Zoom") controls. A document opens fitted to the width of the pane;
#emph[fit page] shrinks it until the whole page is visible at once — useful when the pane is wider than it is
tall, where fitting the width pushes the bottom of the page out of sight. Zooming out after that walks back down
to the whole page and stops there. Ctrl+scroll (⌘+scroll on a Mac) zooms as well.

#shot("screenshots/desktop-search-hit-overlay.png",
  [The preview with search hits highlighted on the page — click a word to copy it (shift-click to append), or
   step through the matches.])

= Adding & versioning documents

== How documents get in

There is more than one way in, and which you want depends on whether the document is finished, whether it
belongs to something already filed, and how many you have. They all end in the same place.

#table(
  columns: (auto, 1fr),
  stroke: 0.5pt + luma(80%),
  inset: 6pt,
  [*Route*], [*Use it when*],
  [*Upload*#idx("Upload") on the ribbon], [You are already looking at the folder it belongs in. Picks files and files them
   straight into the open folder.],
  [*Drag onto a folder* — a row in the list, or a node in the tree], [You can see the destination. The document
   is filed there; the view follows to that folder so you can see it arrive.],
  [*Drag onto empty space* in the contents list], [Same as above for the folder you are already in.],
  [*Drag onto a document*], [The file is a newer copy of that document — it is added as a *version*, not as a
   second document, and the history is kept.],
  [*Drag onto your own space ▸ Intray*#idx("Intray")], [It is not ready to file, or you do not yet know where it belongs. It waits in
   the Intray until you classify and file it.],
  [*Drag a document onto your own space ▸ Intray*], [You want to start from an existing document as a *template*. A
   copy lands in your Intray carrying that document's document type and index data, so you edit what differs.
   Nothing is created in the archive until you file it.],
  [*Drag onto your own space ▸ Check-out*#idx("Check-out")], [You checked a document out, edited it on your computer, and are bringing
   it back. The file must still carry the document's name — that is what says which document it belongs to.],
  [*WebDAV*], [You would rather work in Finder, Explorer or Files. Mount the archive as a drive and copy
   documents in like any other folder.],
  [*Import*#idx("Import / export")], [You are bringing in a whole folder tree at once, exported from SimplArchive or elsewhere.],
  [*Email attachment*], [You filed an email and want one of its attachments as a document of its own.],
)

#shot("screenshots/web-personal-launchers.png",
  [Your own space expanded — it carries *your name* at the top of the tree: *Intray* and *Check-out* sit above your own folders. Drop files on *Intray* to
   stage them, or drop an edited working copy on *Check-out* to bring it back — and drag a document onto *Intray*
   to start new work from it as a template.])

#note[
  *Two of these do not create a document.* A drop onto the *Intray* stages an item — it becomes a document only
  when you file it. A drop onto *Check-out* replaces your working copy — the document gets a new version only
  when you check it in. Everything else files immediately.
]

*Uploading.* Drag a file straight onto a folder (the bytes go directly to object storage — the server never
proxies them). *The Intray* is a staging area: drop scans or files there, then classify each one (name, document
type, index data) and *file* it into the archive.

*What the archive will not keep.*#idx("Executable content") Programs and scripts are refused — an `.exe`, a `.bat`, a
shell script, and anything that turns out to be a program whatever it has been named. Everything else is kept,
including the formats a document system is often too narrow for: drawings, disk images, archives, video. When an
*email's* attachment is refused the message itself is still archived, and a line in its chat thread names the
attachment that was left out, so nothing goes missing quietly.

*When the name is already taken.*#idx("Name conflict") A folder cannot hold two things of the same name, so filing
`Invoice.pdf` where an `Invoice` already sits asks you what you meant rather than refusing:

#table(
  columns: (auto, 1fr),
  stroke: 0.5pt + luma(80%),
  inset: 6pt,
  [*Choice*], [*What happens*],
  [*A new version of that document*], [The usual answer: the file is a newer revision of what is already there.
   The document keeps its index data and its history, and the previous version stays available.],
  [*A new document with a different name*], [The two are genuinely different documents that happen to share a
   file name. A free name is offered — `Invoice (2)` — and you can change it.],
)

Either way you can add a *comment* saying what this file is, which becomes the version's comment. There is no
"overwrite": nothing in SimplArchive replaces content in place, and the honest equivalent is a new version.

#note[
  If the name is held by a *folder* rather than a document, only the second choice is possible — a folder has no
  versions to add one to.
]

*Versions.*#idx("Version comment") Uploading a new file to an existing document adds a *version* — the history is preserved. The
*Versions* dialog lists every version; you can compare two of them or *make current* an older one. When you upload
a file that already exists, SimplArchive warns you of the *duplicate*.

Give each version a *comment* saying what changed. It is the difference between a history someone can read and a
list of timestamps: "Price corrected after the framework-agreement review" answers the question a reader of the
list actually has, and no date can.

#shot("screenshots/web-versions.png",
  [The *Versions* dialog: every version of the document with who saved it, when, and — the part that makes the
   list worth reading — the comment describing what changed.])

#pair("screenshots/web-intray.png", "screenshots/desktop-intray.png",
  [The Intray: staged items waiting to be classified and filed, in the web (left) and desktop (right) clients.])

== Tidying a scan before filing

A scan rarely arrives filing-ready: a batch holds several documents, a page went through the feeder sideways, a
blank back rode along. The Intray is where all of that is put right — *before* filing, because a staged item can
be reshaped freely, while a filed document's pages are part of the record. Every operation here works on `.pdf`
and multi-page `.tif` files; anything else simply does not offer them.

#table(
  columns: (auto, 1fr),
  stroke: 0.5pt + luma(80%),
  inset: 6pt,
  [*Operation*], [*What it does*],
  [*Split into pages*#idx("Split into pages")], [One new item per page. The original is kept, so a split that
   turns out wrong is undone by deleting its output — a scan can be the only copy of a piece of paper.],
  [*Rotate/Sort*#idx("Rotate/Sort")#idx("Sort pages")#idx("Rotate pages")], [Opens the page dialog below: put
   the pages in order, delete a page with the bin on its tile, and turn a page with the *⟲ ⟳* buttons under it.
   Nothing is written until *Apply order* — one save for the whole arrangement, and *Cancel* discards
   everything. Offered from a single page up, because rotating a one-page scan that went in upside-down is
   exactly what it is for.],
  [*Join items*#idx("Join items")], [Several staged items become one, in the order you chose them. The sources
   are kept.],
  [*Cut at separator sheets*#idx("Separator sheets")], [Cuts a batch into one item per document at the printed
   separator sheets between them (see below).],
)

#shot("screenshots/desktop-sort-rotate.png",
  [The *Rotate/Sort* dialog on the sample batch: page 4 went through the scanner upside-down and has
   been turned a quarter so far — one more press of its *⟳* button puts it upright. The bin on each tile
   deletes that page; nothing is saved until *Apply order*.])

*Rotation does not damage a PDF.* Turning a PDF page only records the new orientation — the page's real,
searchable text is untouched. A `.tif` page has no such notion and is re-encoded when turned, the same trade its
straightening makes.

*Separator sheets.* For a stack of paper documents, print the *separator sheet* (the printer button in the
Intray toolbar), lay one between each document, and feed the whole pile through the scanner in one go. *Cut at
separator sheets* then produces one item per document and discards the sheets themselves. Sample files to try
this with — a batch as the scanner would produce it, and the sheet — are served by your own installation under
`/download/samples/`.

*The automatic toggles.* Three sticky switches in the Intray toolbar act on every *arriving* upload — including
files saved into the Intray folder of the mounted network drive:

#table(
  columns: (auto, 1fr),
  stroke: 0.5pt + luma(80%),
  inset: 6pt,
  [*Toggle*], [*What it does to an arriving scan*],
  [*Auto-rotate*#idx("Auto-rotate")], [Turns pages that arrived upside-down or sideways the right way round.
   Works for `.pdf` and `.tif`.],
  [*Auto-straighten*#idx("Auto-straighten (deskew)")], [Corrects the slight skew of a crooked scan — for
   `.tif`, and for a PDF that is itself a pure scan (image pages, no real text). A born-digital PDF is left
   alone: straightening re-renders the pages, and doing that would replace its real text with a recognised
   approximation.],
  [*Cut at separator sheets*], [Applies the separator cut to every arriving batch, so a scanner that feeds
   straight into the Intray needs no manual step at all.],
)

#note[
  *Digitally signed documents are never touched.* Every page operation would break the signature, so on a
  signed item none is offered and the automatic steps skip it — the refusal is the feature.
]

*Already filed?* *Rotate/Sort* also exists on the *Check-out* tab#idx("Rotate/Sort"). It edits the
*working copy* of a document you have checked out — the archived version stays exactly as it was, and your
rearrangement becomes the new version only when you press *Check in* (or is thrown away by *Discard*, like
any other working-copy edit). Split and join stay in the Intray: they turn one item into several documents,
which is filing work.

#shot("screenshots/web-version-compare.png",
  [*Compare versions*: two revisions of a document side by side — old on the left, new on the right, changed
   lines aligned and the changed words highlighted within them. Works for any pair whose content yields text,
   notes and emails included.])

= Organizing

Create *folders*, and *move* documents between them by drag-and-drop. A *reference*#idx("Reference (shortcut)") (shortcut) lets one document
appear in several places without copying it. *Tags*#idx("Tags") label documents for quick grouping — your tenant can maintain a
curated tag catalogue with colours. *Sensitivity labels*#idx("Sensitivity label") mark how confidential a document is. Every user also has a
*personal repository*#idx("Personal repository") for private documents.


#shot("screenshots/web-tags.png",
  [The tag catalogue: curated, colour-coded tags an administrator maintains for the whole tenant.])

= Working from your file manager (WebDAV) <webdav>

The whole archive is reachable over *WebDAV*#idx("WebDAV") as a network drive — not just your personal space but the shared
repositories you have permission to access, with your rights enforced on every operation. The mounted drive is
called *SimplArchive* and mirrors the Repositories tree exactly: your Personal space, then the repositories you
can see, and nothing else.

The *WebDAV* button does the next useful thing rather than always the same thing, and its tooltip says which. In
the *desktop client*: if you have no credentials yet it opens the setup dialog; if you have credentials it mounts
the drive and opens it (on Windows as a persistent drive letter, S: or the next one free); and if the drive is
already mounted it goes straight there. So "show me my documents in Finder" is one click once you are set up.

*It opens where you already are.*#idx("WebDAV deep link") There is one button per tab, and each opens *its own*
folder inside the drive rather than the top of the archive:

#table(
  columns: (auto, 1fr),
  stroke: 0.5pt + luma(80%),
  inset: 6pt,
  [*Where you press it*], [*What opens*],
  [*Repositories* ribbon], [The folder selected in the tree — the drive mirrors the tree exactly, so "where I am"
   and "which folder on the drive" are the same place. With nothing selected, the whole archive opens.],
  [*Intray* tab, lower left], [Your *Intray* folder.],
  [*Check-out* tab, lower left], [Your *Check-out* folder, where working copies live.],
)

#note[
  *If you connect to more than one SimplArchive* — a local one and a hosted one, say — your computer cannot give
  both drives the same name, so the second is called something like *SimplArchive-1*. The button always opens the
  drive belonging to the server you are signed in to, whichever name it ended up with.
]

The setup dialog shows the *mount URL* and your *username* (your e-mail), each with a *Copy* button, and a
*Generate* button that issues an app-specific *WebDAV password* — separate from your login password and shown
only once, so copy it right away. The desktop dialog also has *Open folder*, so you can go from generating
credentials to a mounted drive without closing it.

The *web client* cannot mount a drive: a browser is not allowed to. Instead of leaving you with a URL and no idea
what to do with it, its dialog shows the mount steps for the operating system you are on — Finder's
#emph[Go ▸ Connect to Server] on macOS, *Map network drive* on Windows, a `davs://` address on Linux — next to
the values to paste into them.

#shot("screenshots/web-webdav.png",
  [The web client's WebDAV dialog: the mount URL, your username and the one-shot password, each with a copy
   button, and the mount steps for the operating system you are using.])

== Editing a document in place

Open a document from the mounted drive, change it, save it — that is the whole gesture, and it works the way you
would expect from any network drive.

What happens underneath is worth knowing, because it changes what other people see. Simple editors write the file
straight back, and the change becomes a new version immediately. An *office suite*, however, never overwrites a
file in place: it writes a hidden companion file, then a temporary copy of the new content, and finally renames
that copy over the original. SimplArchive recognises that sequence and turns it into a *check-out* — the same
one you get from pressing *Check out* in the app.

So after saving from your file manager:

- the document is *checked out to you*, and appears on your *Check-out* tab marked *automatic*;
- your edit is waiting there as your working copy — it is *not yet a new version*, and other people still see the
  previous one;
- nobody else can change the document until you are finished;
- you finish by pressing *Check in*, from either client or by saving into the mount's *Check-out* folder.

#note[
  *Saving is not the same as publishing.* The system cannot tell when you have finished editing — only you can,
  and *Check in* is how you say so. Until then your work is kept safely as your working copy. A check-out left
  untouched for a long time is released automatically by your organisation's idle rule, and you are warned before
  that happens.
]

The *automatic* marker exists so this never looks like a fault: a document you never explicitly checked out has
become yours, and the tab says why, naming the program that did it.

If someone else already has the document checked out, your editor is told the file is locked and opens it
read-only, rather than letting you edit for ten minutes and failing at the save. Documents you are not allowed to
edit, and documents frozen by a legal hold, behave in the mount exactly as they do in the app: they refuse.

#note[
  *A warning from your own software is not a fault in ours.* Some commercial vendors treat a WebDAV connection to
  a site that is not on their own allow-list as suspicious, or refuse it outright. That is a policy decision in
  the respective vendors' software, not a limitation of SimplArchive — the same mount that one program declines
  will typically work in another on the same machine.
]

== Reading the archive in your mail program (IMAP) <imap>

Where WebDAV turns the archive into a network drive, *IMAP*#idx("IMAP") lets a mail program browse it: point
any IMAP-speaking mail client at your SimplArchive server and browse the same
ACL-filtered tree as mailboxes. *INBOX* is your personal folder; the shared repositories you can see appear
beside it. Archived e-mails read natively; every *other* document can appear too — as a message carrying the
file as an attachment — if you switch that on.

#note[
  *This is your archive wearing an IMAP face — it is not a mail service.* SimplArchive does not collect your
  mail from your provider, does not send mail, and is not a replacement for your e-mail account. Nothing here
  changes where your mail lives. What it gives you is the second account in your mail program: keep your real
  one alongside it, and *file* mail across from one to the other, which is what the drag described below is
  for.
]

*Shortcuts show up as mailboxes too.* A folder you have filed a reference#idx("Reference") to appears in the
list wherever the reference sits, with everything beneath it — so the destinations you file into most can be
gathered in your personal folder and reached from the mail program without hunting through the whole tree.

Open *Email access (IMAP)…* in the account menu. The dialog shows the *server* and your *username* (your
e-mail), each with a *Copy* button, and *Generate* issues a dedicated *IMAP password* — like the WebDAV one:
separate from your login password, shown only once, revocable on its own. The *Show every document* switch is
yours per account: off (the default) lists only e-mails; on lists everything you may see, with the file
attached to open straight from the mail client.

#note[
  *The mail client cannot rearrange the folders.* Folders are created, renamed and deleted in SimplArchive —
  an IMAP client trying the same is politely refused. Messages, though, are yours to work with: drag an
  e-mail into a folder to *file it into the archive* (it becomes a proper e-mail document, named by its
  subject), move a message to re-file it, copy one to leave a *reference*, and delete + expunge to send it
  to the *recycle bin* — never a hard delete. Read/unread marks are remembered per person.
]

*Notes, too.*#idx("Notes") Point a notes app that syncs over IMAP at the same account and it finds the
*Notes* mailbox — your personal *Notes* folder in disguise. Every note becomes a proper archive document with
the *Note* document type, and editing a note on your phone adds a *new version* in the archive rather than a
new copy — the full history stays browsable in the workbench. The Notes folder is typed: it accepts only
notes, and notes live only there (references to them may go anywhere).

= Contacts, calendars & your tasks

Two of the workbench tabs are not about documents at all — or rather, they are about documents that happen to be
a *contact card* or a *calendar entry*. *Contacts*#idx("Contacts") lists the people in your addressbooks;
*Calendar*#idx("Calendar") lists your appointments. Both read the same archive, with the same permissions, and
both can be *subscribed to from your phone*.

#shot("screenshots/desktop-contacts.png",
  [The Contacts tab: addressbooks on the left, the people in the ticked ones merged into one list.])

*Collections overlay rather than replace.* The left-hand list is a set of tick-boxes, not a single choice — tick
two addressbooks and you see both sets of people in one list, each row carrying a small colour swatch saying
which collection it came from. Your own *My Addressbook* and *My Calendar* start ticked, so the tab opens with
something in it. The colour is yours to set and yours alone: changing it does not change what anybody else sees.

Every user's personal space contains *My Addressbook* and *My Calendar* from the start. They cannot be deleted or
moved — they are the fixed points your phone subscribes to, and provisioning them would be pointless if the next
click could remove them. Addressbooks and calendars can also live *anywhere else* in the archive: a shared
repository can hold a team addressbook, and whoever has permission sees it in the same list.

== Adding and editing a contact or an appointment

*New contact* and *New appointment* open a form. Fill it in and save, and the item is created — nothing exists
until you save, so closing the form leaves nothing behind. If more than one collection is ticked, the form asks
which one it should go into; with only one candidate it does not ask, and the confirmation names where it went.

*Edit* opens the *same* form on an existing item. That is deliberate rather than incidental: a create form with
fewer fields than the editor would quietly discard whatever you typed into the ones it lacked.

A contact holds rather more than a name — several *e-mail addresses* and *phone numbers*, each with its own type,
several *postal addresses*, an organisation, a job title, a birthday, a website and a note. Add and remove rows
as you need them.

#note[
  *Everything the form does not show is kept.* A contact created on a phone carries things this form does not
  model — a photo, custom labels, fields belonging to some other program. Saving here *preserves* all of it
  rather than dropping what it does not understand, which is what makes it safe to edit in the archive a card
  that lives on your phone.
]

== Advanced: the stored item

Under every contact and appointment form there is a collapsed *Advanced: the stored item* section. Open it and
you see the item exactly as stored — a *vCard* for a contact, an *iCalendar* entry for an appointment — including
the properties the form above does not show.

#shot("screenshots/desktop-contact-editor.png",
  [The contact editor with *Advanced: the stored item* open — the stored vCard, including the properties no form
   field shows.],
  width: 42%)

It is editable, and this is the one place in the product where saving *replaces* rather than merges. Delete a line
here and the property is gone; that is what "raw" has to mean, and a merge would put it back while telling you the
save had succeeded. Two things are refused rather than accepted: text that is not a valid card or entry, and a
change to the *UID* — the identifier every phone and mail program uses to recognise this item. Changing the UID
would not rename the item, it would make it a *different* one, so your phone would keep the old copy and add the
new. In both cases nothing is written and the stored item is untouched. Removing the UID line is not a change; the
stored one is kept.

#note[
  *While you are editing the raw text, the fields above go read-only.* Both describe the same item and only one of
  them can be saved. A line under the box says which way the save will go.
]

Every save — from the form or from the raw box — writes a *new version*, exactly as any other change to a
document does. A raw edit you regret is recoverable from the version history like anything else.

== Subscribing from your phone (CalDAV & CardDAV) <davsub>

Addressbooks and calendars speak *CardDAV*#idx("CardDAV") and *CalDAV*#idx("CalDAV") — the standards phones,
tablets and mail programs already use. Point a device at your SimplArchive server and it finds every addressbook
and calendar you are allowed to see, wherever it sits in the archive; you then pick which to sync. Edits travel
both ways: a contact changed on your phone becomes a new version in the archive, and one changed in the workbench
appears on the phone.

The credential is the *same DAV password* the file-manager mount uses — one secret, one place to revoke it. Issue
it from *WebDAV…* in the account menu if you have not already.

#shot("screenshots/desktop-calendar.png",
  [The Calendar tab, ordered by time — undated entries sort last rather than into the top rows.])

== Your tasks, on your phone

Beside your calendars, two more collections appear that hold no documents at all:

- *My tasks* — the reviews assigned to you, as to-do items. This is what a reminders or task app subscribes to.
- *My task deadlines* — the same reviews as dated entries, for calendar programs that do not show to-dos.

Both are *read-only*. They are a view of the *My tasks* tab, published so you can see what is waiting for you
without opening the archive; approving or rejecting a review still happens in the workbench, where it is recorded
with who did it and why.

#note[
  *A review only gets a deadline if its document type defines one.* The review SLA is set per document type, and
  where none is set the review simply has no due date — it still appears in *My tasks*, but it cannot appear in
  *My task deadlines*, which is a list of dates. An empty deadlines list usually means no document type in your
  tenant has an SLA configured.
]

== On a phone or a tablet

The same address, in the same browser, gives you a layout built for the screen you are holding. There is no
separate mobile site and nothing to install — and nothing is *taken away*: what the narrower layouts cannot show
side by side, they show one at a time.

*A tablet is recognised by its touch screen, not by its width.*#idx("Tablet") That distinction matters more than
it sounds: a large tablet is about 1024 points across upright and 1366 across sideways, so measuring width alone
would call the same device a tablet one way up and a desktop the other. Turning your tablet changes how many
panes you get, and never which kind of device the archive thinks you have.

=== A phone: one pane, and a way back

The folder tree becomes a drawer, the folder's contents fill the screen, and a document opens over the top with
its own *Preview*, *Details* and *Comments* tabs — so the panes the wide layout drops are a tap away rather than
gone.

#shot("screenshots/web-phone-list.png",
  [A folder's contents on a phone, filling the screen. A single tap opens — there is no double-click here.], width: 45%)

#shot("screenshots/web-phone-drawer.png",
  [The tree, slid in from the left. Choosing a folder closes it again.], width: 45%)

#shot("screenshots/web-phone-detail.png",
  [A document, full screen, with its three tabs. Back returns to the list.], width: 45%)

#note[
  *A single tap navigates on a phone.* On a desktop a click selects and a double click opens, because there is a
  second pane to show the selection in. On a phone there is not, so a tap goes straight in.
]

=== A tablet: one pane upright, two sideways

Held upright, a tablet behaves exactly as a phone does — one pane, the tree in a drawer.

#shot("screenshots/web-tablet-portrait.png",
  [Upright: one pane at a time, the same shape as a phone.], width: 75%)

Turn it sideways and you get two of the three panes. While you are browsing, the tree sits beside the folder's
contents.

#shot("screenshots/web-tablet-landscape.png",
  [Sideways, browsing: the tree beside the contents list.])

Open a document and the tree steps aside for it, so what you came *from* stays next to what you opened — and the
tree is one tap away from the #box[≡] button whenever you want it back.

#shot("screenshots/web-tablet-landscape-detail.png",
  [Sideways, with a document open: the list beside the document. The tree is a tap away.])

=== What each width shows

#table(
  columns: (auto, 1fr),
  inset: 6pt,
  align: (left, left),
  table.header([*Screen*], [*What you see*]),
  [Desktop], [All four panes — tree, contents, document, comments — resizable, and remembered per browser],
  [Tablet, sideways], [Two panes: tree and contents, or contents and document],
  [Tablet, upright], [One pane, tree in a drawer],
  [Phone], [One pane, tree in a drawer, document full screen],
)

The comments pane is the first to go as the screen narrows, and the index-data pane the next — both come back as
tabs inside the document view, so nothing becomes unreachable. It moves.

#note[
  *Buttons are labelled where you cannot hover.* On a desktop a ribbon button is an icon and its name appears on
  hover. A touch screen has no hover and therefore no tooltip, so the same buttons keep their labels — an
  unlabelled symbol you cannot interrogate is a guess.
]

This chapter is about the archive *in a browser*. For your device's own mail, contacts, calendar and files apps
reaching the archive with no browser at all, see @byod.

= Your own device <byod>

Everything in this chapter is already yours. SimplArchive installs *nothing* on your phone, tablet or laptop —
the archive speaks the protocols those devices came with, so the mail app, address book, calendar and file
manager you already use are the client. There is no app to find, no version to keep current, and nothing to
uninstall when you leave.

That also means the archive obeys the same rules on your device as in the workbench. What you may see is what
you may see; a folder you have no rights to simply is not there, rather than being there and refusing you.

== What speaks what

#table(
  columns: (auto, auto, 1fr),
  inset: 6pt,
  align: (left, left, left),
  table.header([*On your device*], [*Protocol*], [*What you get*]),
  [Mail program], [*IMAP*#idx("IMAP")], [The archive as mail folders — see @imap],
  [Address book], [*CardDAV*#idx("CardDAV")], [Contacts sync both ways — see @davsub],
  [Calendar], [*CalDAV*#idx("CalDAV")], [Calendars and your task deadlines — see @davsub],
  [File manager], [*WebDAV*#idx("WebDAV")], [One mountable drive — see @webdav],
  [Notes app], [IMAP], [Notes with real version history — see @imap],
  [Browser], [—], [The full workbench, on any screen],
)

== One password for your devices

Every protocol above uses the *same DAV password*, issued from *WebDAV…* in the account menu. It is separate
from your sign-in password on purpose: a device holds it indefinitely, so it is the one you revoke when a phone
is lost — without changing how you sign in.

Revoking it disconnects every device at once. There is no per-device credential today; if you need one device
cut off, reissue and re-enter the password on the ones you keep.

== Encryption is not optional

Point your devices at an `https://` address, and make sure the mail account uses *TLS* (port 993). This is not
only good practice — some notes clients *silently refuse* a plaintext connection: they ask the server what it
supports, dislike the answer and hang up, showing you nothing at all. A server that works perfectly from every
other app can look broken from that one, and the reason never appears on screen.

#note[
  *If a device connects but shows nothing*, suspect the transport before the account. A wrong password produces
  an error you can read; a refused plaintext connection produces silence.
]

== What each one does not give you

Naming the boundary is what stops a working feature looking broken:

- *A mail program shows the archive, not the application.* No recycle bin, no workflow, no permissions dialog,
  no search across metadata — those live in the workbench.
- *A mounted drive shows the tree, not the history.* Versions, comments and index data are not files, so they
  do not appear; saving over a file makes a new version, which is the one piece of history the drive can express.
- *Calendars and address books carry their own items only.* A calendar holds appointments; it will not show you
  the documents filed beside it.
- *Task collections are read-only.* You can see what is waiting; approving it is recorded in the workbench, with
  who did it and why.

== On a small screen

The workbench itself adapts rather than sending you to a separate mobile site, and it decides by what you are
holding rather than by how wide the window is — a tablet is recognised by its *touch* screen, so turning it does
not turn it into a laptop.

- *Phone, and a tablet held upright* — one pane at a time. The folder tree slides in from the left, the folder
  contents fill the screen, and opening a document covers it with *Preview*, *Details* and *Comments* tabs. A
  single tap navigates, rather than the desktop's click-then-double-click.
- *Tablet held sideways* — two panes. Browsing shows the tree beside the folder contents; opening a document
  swaps the tree for the document, so what you came from stays next to what you opened. The tree is always one
  tap away, from the ☰ button.
- *Laptop or desktop* — the full workbench, with resizable panes that remember their widths.

= Metadata & classification

Each document has a *mask* (document type) that defines its *index fields* — typed metadata such as an invoice
number or a date. Open the *detail* pane's edit toggle to fill them in. A document also carries a *document date*
and one or more *OCR languages*#idx("OCR") used to make scans searchable.

#note[
  Index fields are validated by the mask — a field marked required must be filled, and format/range rules are
  enforced when you save. Well-known masks (Folder, Basic Entry, e-Mail) exist in every tenant; administrators can
  add more.
]

= Search

The *Search* tab#idx("Search") runs a full-text search across document *content*, *names*, and *index-field* values, ranked by
relevance, with hits highlighted on the preview — so a distinctive word buried in a document's text (a product name
on an invoice, say) finds exactly that document. *Refinement filters* and *facets*#idx("Facets") narrow the results — by document
type, date, sensitivity, and more — and you can *save* a search to re-run or share it later.

#pair("screenshots/web-search.png", "screenshots/desktop-search.png",
  [Full-text search with refinement filters and facets, in the web (left) and desktop (right) clients.])

*Parts of words.*#idx("Partial-word search") Search matches whole words first, which is what keeps the most
relevant document at the top. If that finds nothing, the search is retried looking for your text *inside* words —
so `montage` finds a document that only ever says `Montagehalterung`, and `sechskant` finds the longer compound
containing it. This matters most in German, where one word does the work of several. The second attempt is
slower and its ranking is flatter, so it only runs when the first found nothing at all.

*What the field does not do.* Operators are not interpreted: quotes, `AND`, `field:value` and wildcards are
searched as literal text, deliberately — a query can never fail over a stray character. Structured narrowing —
by document type, owner, year, tags, or a specific index field — lives in the refinement filters beside the
results, not in the text box.

= Collaboration

Documents are collaborative: post *comments*#idx("Chat / comments") in a feed, attach *annotations*#idx("Annotations") (sticky notes, highlights, and shapes)
onto the preview, and *follow*#idx("Follow / subscribe") a document or folder to be *notified*#idx("Notifications") of activity. Set yourself a *reminder*#idx("Reminder"), and
track everything assigned to you on the *My work* dashboard#idx("My work"). To edit a document exclusively, *check it out* — others
see it is locked — then *check in* your changes as a new version.

*The chat thread.* Every document has its own thread, shown beside the preview. Use it for the conversation about
the document — a question about a figure, a note that a revision is on its way — so that discussion stays with the
document instead of in someone's mailbox. Replies nest under the message they answer, and the thread also records
what happened to the document: a new version, a filing, and other activity appear in the same timeline, so reading
it tells you both what people said and what changed.

#shot("screenshots/web-chat.png",
  [The chat pane on a document: a threaded conversation interleaved with the document's own activity — here a
   question and its reply, followed by the two versions as they were saved.], width: 60%)

#pair("screenshots/web-my-work.png", "screenshots/desktop-checkout.png",
  [Left: the *My work* dashboard. Right: the *Check-out* tab listing documents locked for exclusive editing —
   here one edited (_Modified_) and one untouched (_Unchanged_).])

*Seeing your edit before you commit it.*#idx("Working copy") The *Check-out* tab#idx("Check-out") shows what you
are *about to check in*, not what is already archived. Select a document you hold and the pane beside the list
shows its index data and a preview of your *working copy* — the file as you have edited it. That is the version
the *Check in* action will create, so you can look at it before committing, without leaving the tab to go and
find the archived copy (which would show you the one thing that is certainly not your edit).

A document you have edited is marked *Modified*, and only those offer the actions that make sense for an edit:

#table(
  columns: (auto, 1fr),
  stroke: 0.5pt + luma(80%),
  inset: 6pt,
  [*Action*], [*What it does*],
  [*Compare*], [An inline, side-by-side view of what changed between the archived version and your working copy.],
  [*Beyond Compare*], [Opens the same two files in Beyond Compare, if you use it — a desktop-only convenience,
   since a browser is not allowed to launch another application. Not affiliated with SimplArchive; the button
   points you to the vendor if you do not have it.],
  [*Check in*], [Files your working copy as a new version and releases the lock.],
  [*Discard*], [Throws the working copy away and releases the lock, leaving the archived version untouched.],
)

#note[
  If you have not saved anything to your working copy yet, the preview stays empty rather than showing the
  archived version. There is genuinely nothing to preview, and showing the archived file there would be
  answering a different question.
]

= Sharing outside SimplArchive

Everything above assumes the other person has an account. An *external link*#idx("External link") is for when they do not: a plain URL
that opens one document, for anyone who has it, with no sign-in. Use it for the customer who needs the signed
contract or the auditor who needs one invoice — not as a general-purpose way to move documents around.

*Creating one.* Select the document and open *External links…* from the detail pane. Choose when it should expire
and how many times it may be opened, then create it.

#note[
  *The URL is shown once, at creation, and never again.* Copy it before you close the dialog. It appears in no
  list afterwards — deliberately, because a list is read far more widely, and far more casually, than the moment
  you deliberately created something.
]

#shot("screenshots/web-external-link-create.png",
  [Creating an external link. The URL is revealed once, here, and cannot be retrieved later.])

*Managing what you have shared.* *My external links* on the ribbon lists your live links: which document, when it
expires, how many times it has been opened. From a row you can jump to the document, review a link's details, or
*revoke* it. An administrator sees everyone's, filtered by user or group.

A link near its expiry can be *extended* — but only within 30 days of lapsing, by at most 90 days, and measured
from today rather than added to whatever time remains. So extending is a deliberate renewal, not a way to
accumulate an indefinite link.

#note[
  *Revoking does not delete the row.* It stamps it revoked and leaves the record standing — who shared what, with
  effect from when. After a link has leaked, that record is exactly what the investigation needs, and it would be
  gone if revoking tidied it away.
]

#shot("screenshots/web-external-links-list.png",
  [*My external links* — every live link you have shared, with go-to, details and revoke on each row.])

*What the recipient sees.* A single page with the document's name, a picture of its first page, and buttons to
open or download it — with the number of pages marked on the picture when there is more than one, so they know
what they are about to open. Nothing else: no tree, no navigation, no route to any other document.

The picture is drawn when you create the link, not when the recipient opens it, so there is nothing to wait for.
A document with no picture to draw — an archive, a binary — simply shows its name and the buttons.

An unknown, expired, exhausted or revoked link all produce the *same* response. That is deliberate — telling a
stranger which of those they hit would confirm a real link exists and hint at how to reach a usable one.

#shot("screenshots/web-external-link-landing.png",
  [The recipient's view: the document's name and a picture of its first page. This one is a single page, so it
   carries no page-count marker. No account, one document, nothing else reachable from it.])

== The controls an administrator holds

Sharing outward is the one action that leaves the system, so it is gated twice and can be stopped in one move.

- *Two gates on creating a link.* The tenant-wide *Allow external links* switch must be on, and the individual
  needs the *Create external link* right, granted per user or group from *Users & groups*. Either alone is
  enough to prevent sharing; both must be present to allow it. A service account can never create one — only a
  person shares, so there is always someone accountable for a link's existence.
- *The kill switch.* Turning *Allow external links* off is checked when a link is *opened*, not only when one is
  created — so it stops every link already out in the world, not merely future ones. This is the control to reach
  for when something has leaked, and it is worth knowing about before you need it.
- *The caps you set.* *Maximum link lifetime* (default 180 days) and *default access count* (default 5) are the
  rails everyone else shares within. Tighten them to your own policy rather than relying on people choosing well.

= Workflow & records

*Approval workflow.*#idx("Approval workflow") Submit a document for review and it moves through a fixed state machine —
Draft → In Review → Approved / Rejected → Released. Reviewers act on their *Tasks* tab#idx("Tasks") — sortable
by any column (a click on a header; the default puts the nearest deadline on top, overdue in red first) and
narrowable through the visible filter row: document and version text, plus an *Overdue only* switch. Reviews can be reassigned,
and overdue reviews escalate.

*Records management.* A *legal hold*#idx("Legal hold") freezes documents so they cannot be changed or deleted. *Retention*#idx("Retention") policies
dispose of documents once their retention period ends (with review before disposition, if required). Deleted
documents rest in the *Recycle bin*#idx("Recycle bin"), from which an administrator can restore them or purge them permanently.

#pair("screenshots/web-tasks.png", "screenshots/desktop-tasks.png",
  [The Tasks tab — a reviewer's approval queue — in the web (left) and desktop (right) clients.])

#pair("screenshots/web-legal-holds.png", "screenshots/web-retention.png",
  [Records management: *legal holds* (left) freeze documents; *retention* (right) governs disposition.])

= Administration & account

Administrators manage *users & groups*#idx("Users & groups") and the *rights* granted to them, configure *tenant* settings#idx("Tenant"), and review
the tamper-evident *audit trail*#idx("Audit trail") of every security-relevant action. They also curate catalogues (sensitivity
labels, tags), set the *storage quota*#idx("Storage quota"), and run *import / export*. Every user manages their own *account security* —
password, multi-factor authentication (authenticator app or passkeys), and profile photo.

Your own account lives behind *Edit profile…*#idx("Edit profile") in the avatar menu (top right in both clients). It shows which
account you are signed in as, the photo you currently have with a crop to replace it, and a button through to
changing your password. Two-factor authentication, passkeys and the WebDAV password stay as their own entries in
that menu, since each is a separate credential rather than part of your profile.

#pair("screenshots/web-users.png", "screenshots/desktop-users.png",
  [Users & groups administration in the web (left) and desktop (right) clients.])

#pair("screenshots/web-audit.png", "screenshots/desktop-audit.png",
  [The audit trail — an append-only, hash-chained log — in the web (left) and desktop (right) clients.])

#pair("screenshots/web-tenant.png", "screenshots/desktop-tenant.png",
  [Tenant settings in the web (left) and desktop (right) clients.])

The desktop client can connect to *several servers*#idx("Server manager"); its server manager stores a profile (name + address) for
each. A server is not a tenant: one SimplArchive installation hosts many tenants, and which tenant you belong to
follows from the account you sign in with.

#shot("screenshots/desktop-server-manager.png",
  [The desktop server manager: connection profiles for several SimplArchive servers.])

// ─────────────────────────────────────────────────────────────────────────────
#pagebreak()
= Appendix — glossary & links

#note[
  *Repository* — a root folder (a document with no parent). *Mask* — a document type / set of index fields.
  *ACL* — per-document access-control list. *Version* — an immutable snapshot of a document's file. *Legal hold*
  — a freeze that blocks change/deletion. *Retention* — a policy that disposes of documents after a period.
  *vCard* / *iCalendar* — the standard file formats a contact and a calendar entry are stored in (`.vcf` /
  `.ics`). *CardDAV* / *CalDAV* — the standards a phone or mail program uses to sync addressbooks and calendars.
  *UID* — the identifier those programs use to recognise an item across devices; changing it makes a duplicate
  rather than an edit. *SLA* — the review deadline a document type sets, which is what gives a task a due date.
]

Further reading:

- The live API description — the OpenAPI document at `/openapi/v1.json`.
- Desktop-client downloads — the download area at `/download`.

#v(1fr)
#align(center)[#text(size: 8pt, fill: gray)[
  This manual's screenshots are regenerated from the running application on every release, so they never fall out
  of date.
]]

// ─────────────────────────────────────────────────────────────────────────────
= Appendix: the desktop client's log

When the desktop client misbehaves — a preview that stays empty, a server that will not connect, a file that
does not open — its log usually says why. The client writes one rolling log file per day, always at full
detail, so there is nothing to switch on after the fact: whatever just went wrong is already recorded.

*Where to find it.* The easiest way is in the app itself: *Help ▸ Show log folder* opens the folder directly.
The location by operating system:

- Windows — `%APPDATA%\SimplArchive\logs`
- macOS — `~/Library/Application Support/SimplArchive/logs`
- Linux — `~/.config/SimplArchive/logs`

Files are named `simplarchive-YYYYMMDD.log`; the newest one is today's. The log is small by design (it rolls at
4 MB and keeps seven files) and never contains a password or a token, so it is safe to attach to a support
request as it is.

*Watching it live.* Start the client from a terminal with the `--verbose` flag and the same full detail is
printed to the terminal as it happens — useful when reproducing a problem step by step:

- Windows — `SimplArchive.DesktopClient.exe --verbose`
- macOS — `SimplArchive.app/Contents/MacOS/SimplArchive.DesktopClient --verbose`
- Linux — `./SimplArchive.DesktopClient --verbose`

Without the flag a terminal shows only the important lines; the file always carries everything, flag or no
flag.

// ─────────────────────────────────────────────────────────────────────────────
#pagebreak()
= Index

Terms are indexed where the manual explains them, not at every mention — the page listed is the one worth
turning to.

#index-page()
