#import "template.typ": conf, shot, pair, note, accent

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
files, versions, classifies, searches, and governs its documents. It is a showcase of how a senior, AI-driven
software developer can produce a complex, enterprise-grade system in a relatively short period — every feature in
this manual is really implemented and really tested.

You reach the same archive through *two clients*, which mirror each other feature for feature:

- the *web workbench* — a browser application, nothing to install;
- the *desktop client* — a native Windows / macOS / Linux application that can additionally open a document in its
  real desktop program and drag documents in and out of the operating-system file manager.

#pair("screenshots/web-login.png", "screenshots/desktop-logon.png",
  [The two clients: the web sign-in (left) and the desktop logon window (right).])

== Core concepts

#note[
  *Repository* — a top-level archive; it is simply a folder with no parent. *Folder* / *document* — the tree
  inside a repository. *Version* — every change to a document's file is kept as a new version; nothing is
  overwritten. *Mask (document type)* — a template of *index fields* (metadata) attached to a document. *ACL* —
  a per-document access-control list of *rights* (see, read, edit, …) granted to users and groups.
]

= Getting started

*Signing in.* Open the web client and choose *Log in*, or start the desktop client and use its logon window; enter
your e-mail and password. Your organisation may additionally require a second factor (a one-time code or a
passkey). You can pick the interface *language* (English, German, Italian, Spanish) and switch between a *light*
and *dark* appearance at any time.

*The workbench.* After signing in you land on the *Repositories* workbench, laid out as: the *tree* of
repositories and folders, the *contents* list of the selected folder, the *detail* pane (index data) over the
*preview*, and — along the bottom — the *tab bar* that switches between Repositories, Inbox, Search, Tasks and the
rest.

#pair("screenshots/web-repositories.png", "screenshots/desktop-workbench.png",
  [The workbench in the web client (left) and the desktop client (right): tree · contents · detail · preview, with
   the bottom tab bar.])

= Browsing & previewing documents

Expand the *tree* to a folder and its documents appear in the *contents* list, which you can sort by any column.
Selecting a document shows its *index data* in the detail pane and renders a *preview* below — PDFs, images, and
converted Office/e-mail/Markdown documents alike. Full-text search hits are highlighted directly on the preview,
and you can click a word to copy it. In the desktop client you can also *open* the document in its native
application.

#shot("screenshots/desktop-search-hit-overlay.png",
  [The preview with search hits highlighted on the page — click a word to copy it, or step through the matches.])

= Adding & versioning documents

*Uploading.* Drag a file straight onto a folder (the bytes go directly to object storage — the server never
proxies them). *The Inbox* is a staging area: drop scans or files there, then classify each one (name, document
type, index data) and *file* it into the archive.

*Versions.* Uploading a new file to an existing document adds a *version* — the history is preserved. The
*Versions* dialog lists every version; you can compare two of them or *make current* an older one. When you upload
a file that already exists, SimplArchive warns you of the *duplicate*.

#pair("screenshots/web-inbox.png", "screenshots/desktop-inbox.png",
  [The Inbox: staged items waiting to be classified and filed, in the web (left) and desktop (right) clients.])

#shot("screenshots/web-version-compare.png",
  [*Compare versions*: an inline diff of two revisions of a document — added lines marked with `+`, removed lines
   with `-` — so a change between versions is easy to see.])

= Organizing

Create *folders*, and *move* documents between them by drag-and-drop. A *reference* (shortcut) lets one document
appear in several places without copying it. *Tags* label documents for quick grouping — your tenant can maintain a
curated tag catalogue with colours. *Sensitivity labels* mark how confidential a document is. Every user also has a
*personal repository* for private documents.

The whole archive is reachable over *WebDAV* as a network drive — not just your personal space but the shared
repositories you have permission to access, with your rights enforced on every operation. To connect, open the
account menu's *WebDAV* item: it shows the *mount URL* and your *username* (your e-mail), and a *Generate* button
that issues an app-specific *WebDAV password* — separate from your login password and shown only once, so copy it
right away. Mount that URL with those credentials to browse and edit documents from your operating system's file
manager.

#shot("screenshots/web-tags.png",
  [The tag catalogue: curated, colour-coded tags an administrator maintains for the whole tenant.])

= Metadata & classification

Each document has a *mask* (document type) that defines its *index fields* — typed metadata such as an invoice
number or a date. Open the *detail* pane's edit toggle to fill them in. A document also carries a *document date*
and one or more *OCR languages* used to make scans searchable.

#note[
  Index fields are validated by the mask — a field marked required must be filled, and format/range rules are
  enforced when you save. Well-known masks (Folder, Basic Entry, e-Mail) exist in every tenant; administrators can
  add more.
]

= Search

The *Search* tab runs a full-text search across document *content*, *names*, and *index-field* values, ranked by
relevance, with hits highlighted on the preview — so a distinctive word buried in a document's text (a product name
on an invoice, say) finds exactly that document. *Refinement filters* and *facets* narrow the results — by document
type, date, sensitivity, and more — and you can *save* a search to re-run or share it later.

#pair("screenshots/web-search.png", "screenshots/desktop-search.png",
  [Full-text search with refinement filters and facets, in the web (left) and desktop (right) clients.])

= Collaboration

Documents are collaborative: post *comments* in a feed, attach *annotations* (sticky notes, highlights, and shapes)
onto the preview, and *follow* a document or folder to be *notified* of activity. Set yourself a *reminder*, and
track everything assigned to you on the *My work* dashboard. To edit a document exclusively, *check it out* — others
see it is locked — then *check in* your changes as a new version.

#pair("screenshots/web-my-work.png", "screenshots/desktop-checkout.png",
  [Left: the *My work* dashboard. Right: the *Check-out* tab listing documents locked for exclusive editing —
   here one edited (_Modified_) and one untouched (_Unchanged_).])

= Sharing outside SimplArchive

Everything above assumes the other person has an account. An *external link* is for when they do not: a plain URL
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

*What the recipient sees.* A single page with the document's name, a preview, and buttons to open or download it.
Nothing else: no tree, no navigation, no route to any other document.

An unknown, expired, exhausted or revoked link all produce the *same* response. That is deliberate — telling a
stranger which of those they hit would confirm a real link exists and hint at how to reach a usable one.

#shot("screenshots/web-external-link-landing.png",
  [The recipient's view. No account, one document, nothing else reachable from it.])

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

*Approval workflow.* Submit a document for review and it moves through a fixed state machine —
Draft → In Review → Approved / Rejected → Released. Reviewers act on their *Tasks* tab; reviews can be reassigned,
and overdue reviews escalate.

*Records management.* A *legal hold* freezes documents so they cannot be changed or deleted. *Retention* policies
dispose of documents once their retention period ends (with review before disposition, if required). Deleted
documents rest in the *Recycle bin*, from which an administrator can restore them or purge them permanently.

#pair("screenshots/web-tasks.png", "screenshots/desktop-tasks.png",
  [The Tasks tab — a reviewer's approval queue — in the web (left) and desktop (right) clients.])

#pair("screenshots/web-legal-holds.png", "screenshots/web-retention.png",
  [Records management: *legal holds* (left) freeze documents; *retention* (right) governs disposition.])

= Administration & account

Administrators manage *users & groups* and the *rights* granted to them, configure *tenant* settings, and review
the tamper-evident *audit trail* of every security-relevant action. They also curate catalogues (sensitivity
labels, tags), set the *storage quota*, and run *import / export*. Every user manages their own *account security* —
password, multi-factor authentication (authenticator app or passkeys), and profile photo.

#pair("screenshots/web-users.png", "screenshots/desktop-users.png",
  [Users & groups administration in the web (left) and desktop (right) clients.])

#pair("screenshots/web-audit.png", "screenshots/desktop-audit.png",
  [The audit trail — an append-only, hash-chained log — in the web (left) and desktop (right) clients.])

#pair("screenshots/web-tenant.png", "screenshots/desktop-tenant.png",
  [Tenant settings in the web (left) and desktop (right) clients.])

The desktop client can connect to *several servers*; its server manager stores a profile (name + address) for
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
