// Demo offer/quote for the SimplArchive showcase — TWO revisions, so the manual + demo can show the
// "Compare versions" inline text diff (ADR 0502). Compile twice:
//   typst compile --input rev=1 sample-offer.typ sample-offer-v1.pdf
//   typst compile --input rev=2 sample-offer.typ sample-offer-v2.pdf
// Both embedded resources the demo seed uploads as version 1 and version 2 of "Offer 2025-014". The revisions
// differ in real, extractable text (a changed quantity + an added line item + updated totals), so Tika extracts
// each version's text and DiffPlex shows +/- lines.

#let rev = sys.inputs.at("rev", default: "1")
#let v2 = rev == "2"

#set page(paper: "a4", margin: (x: 2cm, y: 2cm))
#set text(font: "Helvetica Neue", size: 10pt, fallback: true)
#let accent = rgb("#0f8a6a") // green — distinct from the invoice's indigo, so the two demo docs read apart

#block(fill: accent, inset: (x: 16pt, y: 14pt), radius: 4pt, width: 100%)[
  #set text(fill: white)
  #grid(columns: (1fr, auto), align: (left + horizon, right + horizon),
    [#text(size: 20pt, weight: "bold")[Acme Corp AG] \ #text(size: 9pt)[Industrie- und Feinwerktechnik]],
    [#text(size: 9pt)[Industriestrasse 12 · 8005 Zürich \ MwSt-Nr. CHE-114.222.333 · www.acme-corp.example]],
  )
]

#v(18pt)
#grid(columns: (1fr, auto), gutter: 20pt,
  [
    #text(size: 8pt, fill: luma(110))[ANGEBOT AN / OFFER TO]
    #v(2pt)
    *Muster Immobilien GmbH* \
    z. Hd. Herr T. Frei \
    Bahnhofstrasse 7 \
    6003 Luzern
  ],
  [
    #text(size: 22pt, weight: "bold", fill: accent)[ANGEBOT]
    #v(4pt)
    #table(columns: 2, stroke: none, inset: (x: 0pt, y: 2pt), align: (left, right),
      [Angebots-Nr.], [*Offer 2025-014*],
      [Revision], [#if v2 [*2 (revidiert)*] else [*1*]],
      [Datum], [#if v2 [14.02.2025] else [07.02.2025]],
      [Gültig bis], [31.03.2025],
    )
  ],
)

#v(16pt)

// Positions differ between the two revisions — a changed quantity and an added line item in rev 2.
#let rows = if v2 {(
  ([1], [Aluminium-Strebenprofil 40×40 mm], [30 m], [14.00], [420.00]),
  ([2], [Edelstahl-Winkelverbinder verzinkt], [80 Stk], [3.20], [256.00]),
  ([3], [Sechskantschraube M8×30 (VE 100)], [4 VE], [9.50], [38.00]),
  ([4], [Montagehalterung Typ MH-12], [8 Stk], [24.00], [192.00]),
)} else {(
  ([1], [Aluminium-Strebenprofil 40×40 mm], [30 m], [14.00], [420.00]),
  ([2], [Edelstahl-Winkelverbinder verzinkt], [60 Stk], [3.20], [192.00]),
  ([3], [Sechskantschraube M8×30 (VE 100)], [4 VE], [9.50], [38.00]),
)}

#table(
  columns: (auto, 1fr, auto, auto, auto),
  align: (center, left, right, right, right),
  stroke: none,
  inset: (x: 6pt, y: 7pt),
  fill: (_, row) => if row == 0 { accent.lighten(85%) } else if calc.odd(row) { luma(248) } else { white },
  table.header([*Pos.*], [*Bezeichnung*], [*Menge*], [*Einzelpreis*], [*Betrag*]),
  ..rows.flatten(),
)

#v(6pt)
#line(length: 100%, stroke: 0.5pt + luma(210))
#align(right)[
  #table(columns: (auto, auto), stroke: none, align: (left, right), inset: (x: 8pt, y: 3pt),
    [Zwischensumme (netto)], [#if v2 [CHF 906.00] else [CHF 650.00]],
    [MwSt 8.1 %], [#if v2 [CHF 73.39] else [CHF 52.65]],
    [#text(weight: "bold")[Angebotssumme]], [#text(weight: "bold", fill: accent)[#if v2 [CHF 979.39] else [CHF 702.65]]],
  )
]

#v(16pt)
#block(fill: luma(248), inset: 12pt, radius: 4pt, width: 100%)[
  #text(size: 9pt)[
    #if v2 [
      *Revision 2:* Menge Pos. 2 auf 80 Stk erhöht und Pos. 4 (Montagehalterung Typ MH-12) ergänzt.
      Angebot freibleibend, gültig bis 31.03.2025. Lieferung ab Lager Zürich innert 5 Arbeitstagen.
    ] else [
      Angebot freibleibend, gültig bis 31.03.2025. Lieferung ab Lager Zürich innert 5 Arbeitstagen.
      Preise exklusive Fracht.
    ]
  ]
]
