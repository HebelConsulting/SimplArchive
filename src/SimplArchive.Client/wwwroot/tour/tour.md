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

**Two tracks** are assembled from the same steps, so a corrected step is corrected once:

| Track | Where | Shape |
|---|---|---|
| **Quick** | the public demo | ~3 minutes, **read-only** — navigate and explain, never write. Other visitors are using the same instance. |
| **Full** | your own `docker compose up` | ~15 minutes, hands-on — upload, file, index, share. The instance is yours. |

---

## Quick track (read-only, safe on the shared demo)

### Step 1 — Where you land

```yaml
anchor: '[data-tour="pane-tree"]'
goal: show the workbench shell
expect: '[data-tour="pane-tree"]' exists and '[data-tour="pane-list"]' exists
say: >
  This is one workbench, not a series of pages. The tree on the left is what you can see; the list beside it is
  what is in the selected folder. Everything else in the app is a tab along the bottom of the same shell.
```

### Step 2 — The archive tree

```yaml
anchor: '[data-tour="pane-tree"]'
goal: point out that the tree shows only what this user may see
expect: '[data-tour="pane-tree"]' has attribute data-tour-roots >= 1
say: >
  A repository is just a document with no parent, so the tree is uniform all the way down. What you see here is
  already filtered by permission — this is not a full list with the forbidden parts greyed out.
```

### Step 3 — A folder's contents

```yaml
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
anchor: '[data-tour="pane-chat"]'
goal: show that discussion lives on the document
expect: '[data-tour="pane-chat"]' exists
say: >
  Comments belong to the document, not to an email thread somebody has to be copied on. The filing history
  appears here too, so the conversation and the record are the same object.
```

### Step 6 — Everything else is a tab

```yaml
anchor: '[data-tour="tab-bar"]'
goal: show the breadth without leaving the shell
expect: '[data-tour="tab-audit"]' exists and '[data-tour="tab-search"]' exists
say: >
  Search, tasks, the recycle bin, legal holds, retention, the audit trail — each is a tab on the same workbench.
  Nothing here is a separate application bolted on.
```

### Step 7 — The audit trail

```yaml
anchor: '[data-tour="tab-audit"]'
action: click '[data-tour="tab-audit"]'
goal: show that everything is recorded
expect: '[data-tour="tab-audit"]' has attribute data-tour-active = "true"
say: >
  Every change is recorded in a hash-chained, append-only log. Worth saying plainly: this is the part that makes
  the rest trustworthy, and it is not something that can be added convincingly afterwards.
```

### Step 8 — Close

```yaml
anchor: '[data-tour="pane-tree"]'
goal: end where you started
expect: '[data-tour="pane-tree"]' exists
say: >
  That is the shape of it: one workbench, permissions that decide what exists rather than what is greyed out, and
  a record of everything. The demo resets nightly, so explore freely — you cannot break anything that matters.
```

---

## Full track (your own instance — writes are fine)

Run the quick track first, then continue. These steps write, so use a local
`docker compose up` instance rather than the shared demo.

*Not yet written.* The anchors the write steps need — upload, filing, index editing, sharing — are not published
yet, and publishing an anchor implies promising to keep it. Better an honestly short tour than one whose second
half quietly stops working. Track: issue #414.
