// Shared styling + helpers for the SimplArchive user manual (ADR 0502).
// Kept deliberately minimal + font-safe (no bespoke fonts) so `typst compile` is reproducible in CI.

#let accent = rgb("#5b4ee6") // matches the app's indigo app-bar

// Document-wide configuration. Wrap the whole manual body in `#show: conf`.
#let conf(version: "", date: "", doc) = {
  set document(title: "SimplArchive — User Manual")
  set page(
    paper: "a4",
    margin: (x: 2.2cm, top: 2.4cm, bottom: 2.2cm),
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
  show heading.where(level: 2): it => {
    block(above: 1.1em, below: 0.6em)[
      #set text(size: 13pt, fill: accent.darken(15%), weight: "bold")
      #it
    ]
  }

  doc
}

// A single captioned screenshot, framed with a thin border.
#let shot(path, caption) = figure(
  block(
    stroke: 0.5pt + luma(200),
    radius: 3pt,
    clip: true,
    image(path, width: 100%),
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
