// Shared styling + helpers for the SimplArchive user manual (ADR 0502).
// Kept deliberately minimal + font-safe (no bespoke fonts) so `typst compile` is reproducible in CI.

// The accent comes from the SAME tokens.json as both clients (issue #513): the manual is a third styled
// surface, and the teal flip proved it — every screenshot regenerated teal while this file's own hardcoded
// purple stayed. ThemeGenerationTests now guards the generated file, so the next rebrand cannot strand it.
#import "colors.generated.typ": accent

// Document-wide configuration. Wrap the whole manual body in `#show: conf`.
// Set by every heading, read and cleared by the first paragraph after it (#699) — see the show rules below.
#let after-heading = state("manual.after-heading", false)

#let conf(version: "", date: "", doc) = {
  set document(title: "SimplArchive — User Manual")
  // Named because the last-third heading rule below computes against the page BODY, and a margin changed
  // here but not there would silently move where "the last third" begins.
  let margin-top = 2.4cm
  let margin-bottom = 2.2cm
  set page(
    paper: "a4",
    margin: (x: 2.2cm, top: margin-top, bottom: margin-bottom),
    numbering: "1",
    footer: context {
      set text(size: 8pt, fill: gray)
      grid(
        columns: (1fr, 1fr),
        align: (left, right),
        [SimplArchive — User Manual],
        counter(page).display("1"),
      )
    },
  )
  set text(size: 10.5pt)
  set par(justify: true, leading: 0.62em)
  set heading(numbering: "1.1")

  // Chapter headings (level 1): start on a new page, coloured, generously spaced.
  show heading.where(level: 1): it => {
    pagebreak(weak: true)
    block(above: 0.4em, below: 0.9em)[
      #set text(size: 20pt, fill: accent, weight: "bold")
      #it
    ]
  }
  // `sticky: true` is NOT decoration — it restores what this rule took away (#699). Typst's own heading block
  // is sticky, meaning it stays with the block that follows; wrapping the heading in a block of our own to
  // style it REPLACES that block and silently drops the property. So every level-2 heading here could be left
  // alone at the foot of a page, which is what "12.1 The controls an administrator holds" was doing: the
  // heading and one line of intro, then a page turn for the list. Level 1 never showed it only because it
  // starts on a fresh page anyway.
  // …and stickiness is not enough when the heading lands LOW (#815): the sticky chain only guarantees the
  // heading is not alone — a sub-chapter opening in the last third of the page still reads as a footnote to
  // the previous topic, with its real content overleaf. So a level-2/3 heading whose top would fall past
  // two-thirds of the page BODY starts on a fresh page instead. Measured against the body (margins excluded),
  // not the sheet, because "the last third of the page" means the last third of what a reader sees as the
  // page. `weak: true` so a heading already at the top of a page (level 1 just broke, or two sub-chapters
  // back to back at a boundary) does not mint a blank page.
  // NOT by measuring the heading's position: "measure, and break if low" moves the heading to the next page,
  // where it measures high, so the next layout pass removes the break — an oscillation Typst reports as
  // "document did not converge" and resolves by shipping whatever pass five produced, page counter (and so
  // TOC and index numbers) included. Latching the decision in a state converges too slowly for the five-pass
  // cap. So the mechanism is the layout engine's own, with no feedback at all: the heading rides inside an
  // UNBREAKABLE block one-third of the body tall — the engine must break the page exactly when less than a
  // third remains — and the flow is then pulled back up by the block's unused remainder, so when the heading
  // fits mid-page nothing changes visually. measure() is pass-stable, unlike here().position().
  let keep-out-of-last-third(above, below, styled) = layout(size => {
    let third = size.height / 3
    let h = measure(block(width: size.width, styled)).height
    block(above: above, below: 0pt, breakable: false, sticky: true, height: third, styled)
    v(h + below - third)
  })
  show heading.where(level: 2): it => keep-out-of-last-third(1.1em, 0.6em, {
    set text(size: 13pt, fill: accent.darken(15%), weight: "bold")
    it
  })
  show heading.where(level: 3): it => keep-out-of-last-third(0.9em, 0.5em, it)

  // ...and stickiness has to reach PAST the intro line (#699). A heading sticks to the block that follows, and a
  // one-line paragraph satisfies that completely — so "heading + one line + page turn" survives a sticky
  // heading, which is what "7.5.2 A tablet" was still doing after the figures were resized: heading, one line,
  // five centimetres of blank, figure overleaf.
  //
  // So the paragraph directly after a heading is sticky too, and the chain heading → intro → figure holds.
  // ORDER MATTERS: this was tried BEFORE the oversized figures were fixed and made things worse — the phone
  // shots were 36cm tall, so the chain could not fit anywhere and left a near-empty page. It is safe now only
  // because no figure exceeds the page body. If a tall figure is ever added without a `width`, this rule will
  // amplify it rather than absorb it.
  //
  // The state is set by the heading and cleared by the first paragraph that reads it, so only the FIRST
  // paragraph of a section is affected; every later one breaks normally.
  show heading: it => { after-heading.update(true); it }
  show par: it => context {
    if after-heading.get() {
      after-heading.update(false)
      block(it, sticky: true)
    } else { it }
  }

  doc
}

// ── Keyword index (#469) ─────────────────────────────────────────────────────
//
// Hand-rolled rather than pulled from a Typst package: a package import fetches from the Typst universe at
// compile time, which puts a network dependency (and a licence to vet) into the one build step that has to work
// offline and reproducibly in CI. `metadata` + `query` are native and do the whole job in a dozen lines.
//
// `#idx("Term")` places an INVISIBLE marker at the point of use — put it right after the word in the prose, so
// the sentence still reads normally and the term appears exactly where a reader would look it up. Marking is
// deliberately manual: an index is a curated list of what someone would search for, and auto-collecting every
// bolded phrase produces a concordance instead.
#let idx(term) = [#metadata(term)#label("idx")]

// The index itself. Groups the markers by term, collapses repeats on one page, and prints "Term  3, 7, 12".
#let index-page() = context {
  let found = (:)
  for entry in query(<idx>) {
    let term = entry.value
    let page-no = counter(page).at(entry.location()).first()
    let pages = found.at(term, default: ())
    // A term marked twice on the same page must not print "7, 7".
    if page-no not in pages {
      found.insert(term, pages + (page-no,))
    }
  }

  // Case-insensitive sort, so "WebDAV" files under W rather than ahead of every lowercase term.
  let terms = found.keys().sorted(key: t => lower(t))

  // Split into two balanced halves BY HAND rather than using `columns`, which fills the first column to the full
  // page height before wrapping — with a short index that leaves one column full and the other empty, which reads
  // as a layout accident rather than an index.
  let entry(term) = {
    set par(justify: false)
    block(below: 0.35em)[#term#h(0.4em)#text(fill: luma(90))[#found.at(term).map(str).join(", ")]]
  }

  if terms.len() > 0 {
    let half = int(calc.ceil(terms.len() / 2))
    grid(
      columns: (1fr, 1fr),
      gutter: 18pt,
      terms.slice(0, half).map(entry).join(),
      terms.slice(half).map(entry).join(),
    )
  }
}

// A single captioned screenshot, framed with a thin border.
//
// `width` exists for TALL figures. A landscape screenshot at 100% of the text width is comfortably shorter than
// a page; a portrait one — a dialog, say — is not: 520x1100 scaled to the text width becomes about twice the
// height of the page, so Typst clips it and drops the caption onto the footer. Narrowing it is what makes a
// portrait figure fit, and the value is chosen per figure because only the figure knows its own shape.
#let shot(path, caption, width: 100%) = figure(
  block(
    stroke: 0.5pt + luma(200),
    radius: 3pt,
    clip: true,
    image(path, width: width),
  ),
  caption: caption,
)

// A web + desktop pair, side by side, sharing one caption — the "both clients" figure the manual leans on.
#let pair(web, desktop, caption) = figure(
  grid(
    columns: (1fr, 1fr),
    gutter: 8pt,
    block(stroke: 0.5pt + luma(200), radius: 3pt, clip: true, image(web, width: 100%)),
    block(stroke: 0.5pt + luma(200), radius: 3pt, clip: true, image(desktop, width: 100%)),
  ),
  caption: caption,
)

// A short "at a glance" callout box for key terms / tips.
#let note(body) = block(
  fill: accent.lighten(92%),
  stroke: (left: 2pt + accent),
  inset: 8pt,
  radius: 2pt,
  width: 100%,
  body,
)
