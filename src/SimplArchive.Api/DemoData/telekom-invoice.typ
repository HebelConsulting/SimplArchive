// Demo monthly invoice for the MyCountry Telekom contract (ADR 0214 demo seed). Compiled THREE times with a
// month input: `--input m=jan|feb|mar` → telekom-invoice-{jan,feb,mar}.pdf. Each is filed under
// Contracts/MyCountry Telekom/Invoices and *referenced* into the matching Business Years/2026 month folder
// (the reference / multi-filing showcase). Real selectable text for full-text search.

#let m = sys.inputs.at("m", default: "jan")
#let info = (
  jan: ("January", "01", "TEL-2026-0001", "01.01.2026"),
  feb: ("February", "02", "TEL-2026-0002", "01.02.2026"),
  mar: ("March", "03", "TEL-2026-0003", "01.03.2026"),
).at(m)

#set page(paper: "a4", margin: (x: 2cm, y: 2cm))
#set text(font: "Helvetica Neue", size: 10pt, fallback: true)
#let accent = rgb("#1e5fa8")

#block(fill: accent, inset: (x: 16pt, y: 14pt), radius: 4pt, width: 100%)[
  #set text(fill: white)
  #text(size: 20pt, weight: "bold")[MyCountry Telekom] #h(1fr) #text(size: 9pt)[Monthly invoice]
]

#v(18pt)
#grid(columns: (1fr, auto), gutter: 20pt,
  [
    #text(size: 8pt, fill: luma(110))[BILL TO]
    #v(2pt)
    *Demo Company AG* \
    Musterweg 1 · 6003 Luzern \
    Customer No. TEL-CUST-4417
  ],
  [
    #text(size: 22pt, weight: "bold", fill: accent)[INVOICE]
    #v(4pt)
    #table(columns: 2, stroke: none, inset: (x: 0pt, y: 2pt), align: (left, right),
      [Invoice No.], [*#info.at(2)*],
      [Billing month], [*#info.at(0) 2026*],
      [Invoice date], [#info.at(3)],
      [Contract], [TEL-2026-00417],
    )
  ],
)

#v(16pt)
#table(
  columns: (1fr, auto, auto),
  align: (left, right, right),
  stroke: none,
  inset: (x: 6pt, y: 7pt),
  fill: (_, row) => if row == 0 { accent.lighten(85%) } else if calc.odd(row) { luma(248) } else { white },
  table.header([*Description*], [*Qty*], [*Amount*]),
  [Business fibre 1 Gbit/s — #info.at(0) 2026], [1], [89.00],
  [Mobile subscription (5 SIM) — flat], [5], [100.00],
  [Excess data], [—], [12.50],
)

#v(6pt)
#line(length: 100%, stroke: 0.5pt + luma(210))
#align(right)[
  #table(columns: (auto, auto), stroke: none, align: (left, right), inset: (x: 8pt, y: 3pt),
    [Subtotal (net)], [CHF 201.50],
    [VAT 8.1 %], [CHF 16.32],
    [#text(weight: "bold")[Total due]], [#text(weight: "bold", fill: accent)[CHF 217.82]],
  )
]

#v(20pt)
#text(size: 9pt)[Payable within 30 days to MyCountry Telekom AG. Please quote invoice *#info.at(2)*.]
