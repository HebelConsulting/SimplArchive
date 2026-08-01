// Demo invoice for the SimplArchive showcase (ADR 0214 demo seed / ADR 0502 manual).
// Compiled to sample-invoice.pdf (an embedded resource the demo seed uploads as "Invoice 2025-001").
// Real, selectable text — so full-text search (Tika + OpenSearch) and search-highlighting work on it.
// Contains "Acme Corp AG" (relied on by WebSearchHighlightTests) and several *distinctive, rare* line-item
// terms (Wolframcarbid, Neodym, Zirkoniumoxid, Molybdändraht) that are well-suited to demonstrate content search
// — a rare word finds exactly this invoice.

#set page(paper: "a4", margin: (x: 2cm, y: 2cm))
#set text(font: "Helvetica Neue", size: 10pt, fallback: true)

#let accent = rgb("#5b4ee6")

// ── Vendor header band ────────────────────────────────────────────────────────
#block(fill: accent, inset: (x: 16pt, y: 14pt), radius: 4pt, width: 100%)[
  #set text(fill: white)
  #grid(columns: (1fr, auto), align: (left + horizon, right + horizon),
    [#text(size: 20pt, weight: "bold")[Acme Corp AG] \ #text(size: 9pt)[Industrie- und Feinwerktechnik]],
    [#text(size: 9pt)[Industriestrasse 12 · 8005 Zürich \ MwSt-Nr. CHE-114.222.333 · www.acme-corp.example]],
  )
]

#v(18pt)

// ── Invoice meta + bill-to ────────────────────────────────────────────────────
#grid(columns: (1fr, auto), gutter: 20pt,
  [
    #text(size: 8pt, fill: luma(110))[RECHNUNG AN / BILL TO]
    #v(2pt)
    *Muster Immobilien GmbH* \
    z. Hd. Frau R. Bircher \
    Bahnhofstrasse 7 \
    6003 Luzern
  ],
  [
    #text(size: 22pt, weight: "bold", fill: accent)[RECHNUNG]
    #v(4pt)
    #table(columns: 2, stroke: none, inset: (x: 0pt, y: 2pt), align: (left, right),
      [Rechnungs-Nr.], [*Invoice 2025-001*],
      [Rechnungsdatum], [31.01.2025],
      [Fällig bis], [28.02.2025],
      [Kunden-Nr.], [KD-4471],
    )
  ],
)

#v(16pt)

// ── Line items (Positionen) ───────────────────────────────────────────────────
#let money(x) = [#x.at(0) #h(1fr) CHF #x.at(1)]

#table(
  columns: (auto, 1fr, auto, auto, auto),
  align: (center, left, right, right, right),
  stroke: none,
  inset: (x: 6pt, y: 7pt),
  fill: (_, row) => if row == 0 { accent.lighten(85%) } else if calc.odd(row) { luma(248) } else { white },
  table.header([*Pos.*], [*Bezeichnung*], [*Menge*], [*Einzelpreis*], [*Betrag*]),
  [1], [Wolframcarbid-Präzisionsfräser WCF-820], [4 Stk], [96.00], [384.00],
  [2], [Neodym-Hochleistungsmagnet N52, Ø45×22 mm], [20 Stk], [12.50], [250.00],
  [3], [Zirkoniumoxid-Dichtungsring, hitzebeständig], [10 Stk], [18.00], [180.00],
  [4], [Molybdändraht 0.30 mm, Rolle à 100 m], [2 Stk], [210.00], [420.00],
)

#v(6pt)
#line(length: 100%, stroke: 0.5pt + luma(210))
#align(right)[
  #table(columns: (auto, auto), stroke: none, align: (left, right), inset: (x: 8pt, y: 3pt),
    [Zwischensumme (netto)], [CHF 1'234.00],
    [MwSt 8.1 %], [CHF 99.95],
    [#text(weight: "bold")[Rechnungsbetrag]], [#text(weight: "bold", fill: accent)[CHF 1'333.95]],
  )
]

#v(20pt)
#block(fill: luma(248), inset: 12pt, radius: 4pt, width: 100%)[
  #text(size: 9pt)[
    *Zahlbar innert 30 Tagen netto* auf IBAN CH93 0076 2011 6238 5295 7, lautend auf Acme Corp AG.
    Bei Fragen zu dieser Rechnung wenden Sie sich bitte an debitoren\@acme-corp.example unter Angabe der
    Rechnungsnummer *Invoice 2025-001*. Alle Positionen wurden gemäss Lieferschein LS-8841 geliefert und geprüft.
  ]
]

#v(1fr)
#align(center)[#text(size: 8pt, fill: luma(140))[
  Acme Corp AG · Industriestrasse 12 · 8005 Zürich · Vielen Dank für Ihren Auftrag.
]]
