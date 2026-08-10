# SimplArchive — a guided tour for your AI

This is a **score, not a recording**. It describes what is worth showing in SimplArchive and what should be true
at each point. Your AI performs it: drives the browser, narrates in your language, at your pace and depth, and
edits the result however you like.

We publish the script because we cannot publish *your* video — not in your language, not at your level of
interest, not focused on the part you care about. That is the half your own agent does far better than we could.

## For the agent reading this

**Anchors.** Every step names an element by a `data-tour` attribute — `[data-tour="pane-list"]`. These are a
deliberate, stable contract: the DOM around them may be reorganised freely, the anchor names may not change
without changing this file. Do **not** anchor on CSS classes, element structure, or visible text. Visible text is
translated (English, German, Italian, Spanish), so any assertion on it is only valid in one locale — which is
precisely the audience this tour is not for.

**Assertions.** Each `expect` is machine-readable and language-independent: an anchor being present, or a
`data-tour-*` value. `[data-tour="pane-list"][data-tour-rows]` carries the row count; `[data-tour="tab-*"]`
carries `data-tour-active="true|false"`. If an `expect` does not hold, the tour is out of date — say so rather
than improvising, and please open an issue.

**Narration.** The `say` lines are beats, not a script to read verbatim. Rephrase for your audience. Say less to
someone impatient; say more about permissions to an administrator.

**Tooling is yours.** Nothing here names a browser driver, a recorder or an editor. Those change; the product's
structure is what we can promise to keep true.

**Two tracks, one list of steps.** Every step carries a `tracks:` key saying which tracks it belongs to. Read the
steps in order and perform the ones tagged with your track; skip the rest. There is no second document to fall out
of date with this one — a corrected step is corrected once, for both tracks.

| Track | Where | Shape |
|---|---|---|
| **Quick** | the public demo | ~3 minutes, **read-only** — navigate and explain, never write. Other visitors are using the same instance, and it resets nightly. |
| **Full** | your own `docker compose up` | ~15 minutes, hands-on — upload, file, index, share. The instance is yours, so it can show the product actually working. |

**Pick the track by where you are.** If the address is the public demo, you are on the quick track and must not
run `full` steps: they write, and someone else may be watching the same screen. On your own instance, run
everything — the full track includes every quick step, so the narration still builds in order.

---

## The steps

In order. Perform the ones whose `tracks:` includes yours.

### Step 1 — Where you land

```yaml
tracks: [quick, full]
anchor: '[data-tour="pane-tree"]'
goal: show the workbench shell
expect: '[data-tour="pane-tree"]' exists and '[data-tour="pane-list"]' exists
say: >
  This is one workbench, not a series of pages. The tree on the left is what you can see; the list beside it is
  what is in the selected folder. Everything else in the app is a tab along the bottom of the same shell.
```

### Step 2 — The archive tree

```yaml
tracks: [quick, full]
anchor: '[data-tour="pane-tree"]'
goal: point out that the tree shows only what this user may see
expect: '[data-tour="pane-tree"]' has attribute data-tour-roots >= 1
say: >
  A repository is just a document with no parent, so the tree is uniform all the way down. What you see here is
  already filtered by permission — this is not a full list with the forbidden parts greyed out.
```

### Step 3 — A folder's contents

```yaml
tracks: [quick, full]
anchor: '[data-tour="pane-list"]'
action: click a folder in '[data-tour="pane-tree"]'
goal: show the contents list responding to the tree
expect: '[data-tour="pane-list"]' has attribute data-tour-rows >= 1
say: >
  Selecting a folder lists what is in it. The row count you see is what the server said you may see — the same
  rule as the tree.
```

### Step 4 — Index data beside the document

```yaml
tracks: [quick, full]
anchor: '[data-tour="pane-index"]'
action: click a document row in '[data-tour="pane-list"]'
goal: show metadata and preview side by side
expect: '[data-tour="pane-index"]' exists and '[data-tour="pane-preview"]' exists
say: >
  Index data sits beside the preview, not on another screen. You can check what was filed against what you are
  actually looking at — which is the whole job of a document management system.
```

### Step 5 — The conversation on the document

```yaml
tracks: [quick, full]
anchor: '[data-tour="pane-chat"]'
goal: show that discussion lives on the document
expect: '[data-tour="pane-chat"]' exists
say: >
  Comments belong to the document, not to an email thread somebody has to be copied on. The filing history
  appears here too, so the conversation and the record are the same object.
```

### Step 6 — Everything else is a tab

```yaml
tracks: [quick, full]
anchor: '[data-tour="tab-bar"]'
goal: show the breadth without leaving the shell
expect: '[data-tour="tab-audit"]' exists and '[data-tour="tab-search"]' exists
say: >
  Search, tasks, the recycle bin, legal holds, retention, the audit trail — each is a tab on the same workbench.
  Nothing here is a separate application bolted on.
```

### Step 7 — The audit trail

```yaml
tracks: [quick, full]
anchor: '[data-tour="tab-audit"]'
action: click '[data-tour="tab-audit"]'
goal: show that everything is recorded
expect: '[data-tour="tab-audit"]' has attribute data-tour-active = "true"
say: >
  Every change is recorded in a hash-chained, append-only log. Worth saying plainly: this is the part that makes
  the rest trustworthy, and it is not something that can be added convincingly afterwards.
```

### Step 8 — Close (quick track)

```yaml
tracks: [quick]
anchor: '[data-tour="pane-tree"]'
goal: end where you started
expect: '[data-tour="pane-tree"]' exists
say: >
  That is the shape of it: one workbench, permissions that decide what exists rather than what is greyed out, and
  a record of everything. The demo resets nightly, so explore freely — you cannot break anything that matters.
```

---

## Hands-on steps (full track only)

These write. They assume your own `docker compose up` instance — on the shared demo they would change what
someone else is looking at.

### Step 9 — Make a folder of your own

```yaml
tracks: [full]
anchor: '[data-tour="action-new-folder"]'
action: select a repository in '[data-tour="pane-tree"]', then click '[data-tour="action-new-folder"]' and name it
goal: show that structure is yours to make, not a fixed hierarchy
expect: '[data-tour="pane-list"]' has attribute data-tour-rows >= 1
say: >
  A folder is not a special kind of object here — it is a document that happens to have no file attached, in the
  same tree as everything else. That is why the same permissions and the same audit trail cover it.
```

### Step 10 — Put a document in it

```yaml
tracks: [full]
anchor: '[data-tour="pane-list"]'
action: drag a file from your desktop onto '[data-tour="pane-list"]'
goal: show filing by drag-and-drop, and that the browser uploads directly to storage
expect: '[data-tour="pane-list"]' has attribute data-tour-rows increased by 1
say: >
  The file went straight from your machine to object storage — the application server never touched the bytes, it
  only said where they belong. That is what lets this scale to documents nobody wants to stream through an API.
```

### Step 11 — Say what it is

```yaml
tracks: [full]
anchor: '[data-tour="action-edit-index"]'
action: select the new document, click '[data-tour="action-edit-index"]', fill a field, click '[data-tour="action-save-index"]'
goal: show index data as structured metadata, not tags bolted on
expect: '[data-tour="action-save-index"]' exists while editing, and is gone after saving
say: >
  What you can fill in comes from the mask — the document type. So "invoice" and "contract" ask for different
  things, and the answers stay searchable as fields rather than as free text buried in the file.
```

### Step 12 — Decide who else sees it

```yaml
tracks: [full]
anchor: '[data-tour="action-manage-access"]'
action: with the document selected, click '[data-tour="action-manage-access"]'
goal: show permissions as something granted on the object, inherited down the tree
expect: '[data-tour="action-manage-access"]' exists
say: >
  Rights are granted here and inherited by everything beneath, unless a folder deliberately breaks that chain. The
  effective view shows what a person actually ends up with, which is the question you usually need answered — and
  it is why the tree earlier showed only what you may see.
```

### Step 13 — Find it again

```yaml
tracks: [full]
anchor: '[data-tour="tab-search"]'
action: open '[data-tour="tab-search"]' and search for a word from the document you filed
goal: show full-text search over content, not just names
expect: '[data-tour="tab-search"][data-tour-active="true"]'
say: >
  Search reads inside the documents — extracted text, OCR for scans — so you can find a contract by a clause
  rather than by remembering what you called the file. The index-field values you just filled in are searchable
  the same way.
```

### Step 14 — Close (full track)

```yaml
tracks: [full]
anchor: '[data-tour="tab-audit"]'
action: open '[data-tour="tab-audit"]'
goal: end on the record of what the tour itself just did
expect: '[data-tour="tab-audit"][data-tour-active="true"]'
say: >
  Everything you just did is in here — the folder, the upload, the metadata, the grant. Not as a side effect you
  could switch off, but as an append-only, hash-chained record. Ending here is the point: the parts that make a
  document system trustworthy are the ones you only notice when you go looking.
```
