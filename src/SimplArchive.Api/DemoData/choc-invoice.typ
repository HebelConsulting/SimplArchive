// Demo invoice — "Invoice for customer's chocolate gift" (ADR 0214 demo seed / ADR 0502 manual).
// Compiled TWICE (Compare-versions showcase): `typst compile --input v=1` → choc-invoice-v1.pdf,
// `--input v=2` → choc-invoice-v2.pdf. v2 corrects the quantity + adds gift-wrapping, so Tika extracts
// different text per version and the inline diff has something real to highlight. Selectable text carries
// the distinctive term "Confiserie Sprüngli-style pralinés" for a content-search demo.

#let ver = int(sys.inputs.at("v", default: "1"))
#let qty = if ver == 1 { "12" } else { "24" }
#let subtotal = if ver == 1 { "168.00" } else { "336.00" }
#let vat = if ver == 1 { "13.61" } else { "27.22" }
#let total = if ver == 1 { "181.61" } else { "363.22" }

#set page(paper: "a4", margin: (x: 2cm, y: 2cm))
#set text(font: "Helvetica Neue", size: 10pt, fallback: true)
#let accent = rgb("#8d4e2a")

#block(fill: accent, inset: (x: 16pt, y: 14pt), radius: 4pt, width: 100%)[
  #set text(fill: white)
  #grid(columns: (1fr, auto), align: (left + horizon, right + horizon),
    [#text(size: 20pt, weight: "bold")[Praliné Manufaktur GmbH] \ #text(size: 9pt)[Feinste Confiserie seit 1998]],
    [#text(size: 9pt)[Konfektgasse 4 · 8001 Zürich \ www.praline-manufaktur.example]],
  )
]

#v(18pt)
#grid(columns: (1fr, auto), gutter: 20pt,
  [
    #text(size: 8pt, fill: luma(110))[BILL TO]
    #v(2pt)
    *Demo Company AG* \
    z. Hd. Einkauf \
    Musterweg 1 \
    6003 Luzern
  ],
  [
    #text(size: 22pt, weight: "bold", fill: accent)[RECHNUNG]
    #v(4pt)
    #table(columns: 2, stroke: none, inset: (x: 0pt, y: 2pt), align: (left, right),
      [Rechnungs-Nr.], [*CHOC-2026-03*],
      [Rechnungsdatum], [16.03.2026],
      [Version], [*#ver*],
    )
  ],
)

#v(16pt)
#table(
  columns: (auto, 1fr, auto, auto, auto),
  align: (center, left, right, right, right),
  stroke: none,
  inset: (x: 6pt, y: 7pt),
  fill: (_, row) => if row == 0 { accent.lighten(85%) } else if calc.odd(row) { luma(248) } else { white },
  table.header([*Pos.*], [*Bezeichnung*], [*Menge*], [*Einzelpreis*], [*Betrag*]),
  [1], [Confiserie Sprüngli-style pralinés, Geschenkbox], [#qty Stk], [14.00], [#subtotal],
  ..(if ver == 2 { ([2], [Geschenkverpackung & Grußkarte], [1 Pauschal], [inkl.], [0.00]) } else { () }),
)

#v(6pt)
#line(length: 100%, stroke: 0.5pt + luma(210))
#align(right)[
  #table(columns: (auto, auto), stroke: none, align: (left, right), inset: (x: 8pt, y: 3pt),
    [Zwischensumme (netto)], [CHF #subtotal],
    [MwSt 8.1 %], [CHF #vat],
    [#text(weight: "bold")[Rechnungsbetrag]], [#text(weight: "bold", fill: accent)[CHF #total]],
  )
]

#v(20pt)
#block(fill: luma(248), inset: 12pt, radius: 4pt, width: 100%)[
  #text(size: 9pt)[
    Vielen Dank für Ihre Bestellung des Kunden-Geschenks. Zahlbar innert 30 Tagen netto.
    Bei Fragen wenden Sie sich bitte an rechnung\@praline-manufaktur.example unter Angabe von *CHOC-2026-03*.
  ]
]
