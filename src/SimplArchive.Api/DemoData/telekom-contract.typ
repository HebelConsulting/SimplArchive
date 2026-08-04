// Demo contract — "MyCountry Telekom — Mobile & Internet Service Agreement" (ADR 0214 demo seed).
// Compiled to telekom-contract.pdf. Real selectable text so full-text search + preview have content.

#set page(paper: "a4", margin: (x: 2cm, y: 2cm))
#set text(font: "Helvetica Neue", size: 10pt, fallback: true)
#let accent = rgb("#1e5fa8")

#block(fill: accent, inset: (x: 16pt, y: 14pt), radius: 4pt, width: 100%)[
  #set text(fill: white)
  #text(size: 20pt, weight: "bold")[MyCountry Telekom] #h(1fr) #text(size: 9pt)[Service Agreement]
]

#v(16pt)
#text(size: 16pt, weight: "bold", fill: accent)[Mobile & Internet Service Agreement]
#v(4pt)
#table(columns: 2, stroke: none, inset: (x: 0pt, y: 2pt), align: (left, right),
  [Contract No.], [*TEL-2026-00417*],
  [Customer], [*Demo Company AG*],
  [Effective date], [01.01.2026],
  [Minimum term], [24 months],
)

#v(14pt)
#text(weight: "bold")[1. Scope of services] \
MyCountry Telekom provides the Customer with one business fibre-internet line (1 Gbit/s symmetric) and five
mobile subscriptions with unlimited domestic calls and 40 GB data per SIM.

#v(8pt)
#text(weight: "bold")[2. Charges] \
The recurring monthly charge is CHF 189.00 (excl. VAT), invoiced monthly in advance. Usage beyond the included
volume is billed per the price list in Annex A.

#v(8pt)
#text(weight: "bold")[3. Term & termination] \
This agreement runs for a minimum term of 24 months and renews for successive 12-month periods unless terminated
in writing with three months' notice before the end of the current term.

#v(8pt)
#text(weight: "bold")[4. Service levels] \
MyCountry Telekom targets 99.9 % availability for the fibre line, measured monthly. Credits per Annex B apply if
the target is missed.

#v(24pt)
#grid(columns: (1fr, 1fr), gutter: 30pt,
  [#line(length: 100%, stroke: 0.5pt) #v(2pt) MyCountry Telekom AG],
  [#line(length: 100%, stroke: 0.5pt) #v(2pt) Demo Company AG],
)
