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

= Getting started

*Signing in.* Open the web client and choose *Log in*, or start the desktop client and use its logon window; enter
your e-mail and password. Your organisation may additionally require a second factor#idx("Multi-factor authentication") (a one-time code or a
passkey). You can pick the interface *language* (English, German, Italian, Spanish) and switch between a *light*
and *dark* appearance at any time.

*The workbench.* After signing in you land on the *Repositories* workbench, laid out as: the *tree* of
repositories and folders, the *contents* list of the selected folder, the *detail* pane (index data) over the
*preview*#idx("Preview"), and — along the bottom — the *tab bar* that switches between Repositories, Inbox, Search, Tasks and the
rest.

#pair("screenshots/web-repositories.png", "screenshots/desktop-workbench.png",
  [The workbench in the web client (left) and the desktop client (right): tree · contents · detail · preview, with
   the bottom tab bar.])

= Browsing & previewing documents

Expand the *tree* to a folder and its documents appear in the *contents* list, which you can sort by any column.
Selecting a document shows its *index data* in the detail pane and renders a *preview* below — PDFs, images, and
converted Office/e-mail/Markdown documents alike. Full-text search hits are highlighted directly on the preview#idx("Hit highlighting"),
and you can click a word to copy it. In the desktop client you can also *open* the document in its native
application.

#shot("screenshots/desktop-search-hit-overlay.png",
  [The preview with search hits highlighted on the page — click a word to copy it, or step through the matches.])

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
  [*Drag onto Personal ▸ Inbox*#idx("Inbox")], [It is not ready to file, or you do not yet know where it belongs. It waits in
   the Inbox until you classify and file it.],
  [*Drag a document onto Personal ▸ Inbox*], [You want to start from an existing document as a *template*. A
   copy lands in your Inbox carrying that document's document type and index data, so you edit what differs.
   Nothing is created in the archive until you file it.],
  [*Drag onto Personal ▸ Check-out*#idx("Check-out")], [You checked a document out, edited it on your computer, and are bringing
   it back. The file must still carry the document's name — that is what says which document it belongs to.],
  [*WebDAV*], [You would rather work in Finder, Explorer or Files. Mount the archive as a drive and copy
   documents in like any other folder.],
  [*Import*#idx("Import / export")], [You are bringing in a whole folder tree at once, exported from SimplArchive or elsewhere.],
  [*Email attachment*], [You filed an email and want one of its attachments as a document of its own.],
)

#shot("screenshots/web-personal-launchers.png",
  [The *Personal* space expanded: *Inbox* and *Check-out* sit above your own folders. Drop files on *Inbox* to
   stage them, or drop an edited working copy on *Check-out* to bring it back — and drag a document onto *Inbox*
   to start new work from it as a template.])

#note[
  *Two of these do not create a document.* A drop onto the *Inbox* stages an item — it becomes a document only
  when you file it. A drop onto *Check-out* replaces your working copy — the document gets a new version only
  when you check it in. Everything else files immediately.
]

*Uploading.* Drag a file straight onto a folder (the bytes go directly to object storage — the server never
proxies them). *The Inbox* is a staging area: drop scans or files there, then classify each one (name, document
type, index data) and *file* it into the archive.

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

#pair("screenshots/web-inbox.png", "screenshots/desktop-inbox.png",
  [The Inbox: staged items waiting to be classified and filed, in the web (left) and desktop (right) clients.])

#shot("screenshots/web-version-compare.png",
  [*Compare versions*: an inline diff of two revisions of a document — added lines marked with `+`, removed lines
   with `-` — so a change between versions is easy to see.])

= Organizing

Create *folders*, and *move* documents between them by drag-and-drop. A *reference*#idx("Reference (shortcut)") (shortcut) lets one document
appear in several places without copying it. *Tags*#idx("Tags") label documents for quick grouping — your tenant can maintain a
curated tag catalogue with colours. *Sensitivity labels*#idx("Sensitivity label") mark how confidential a document is. Every user also has a
*personal repository*#idx("Personal repository") for private documents.


#shot("screenshots/web-tags.png",
  [The tag catalogue: curated, colour-coded tags an administrator maintains for the whole tenant.])

= Working from your file manager (WebDAV)

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
  [*Inbox* tab, lower left], [Your *Inbox* folder.],
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
   question and its reply, followed by the two versions as they were saved.])

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
Draft → In Review → Approved / Rejected → Released. Reviewers act on their *Tasks* tab#idx("Tasks"); reviews can be reassigned,
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
#pagebreak()
= Index

Terms are indexed where the manual explains them, not at every mention — the page listed is the one worth
turning to.

#index-page()
