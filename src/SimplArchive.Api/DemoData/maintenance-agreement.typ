// Demo maintenance agreement for the SimplArchive showcase — the seed's only MULTI-PAGE document (#492).
//   typst compile maintenance-agreement.typ maintenance-agreement.pdf
//
// Two pages on purpose, and it is the reason this document exists. The patch-code sample batch needs a
// document that genuinely spans pages: with none, "cut at the separator sheets" and "split every page" give
// the same answer, and the fixture stops testing the feature it exists for. Stacking two single-page documents
// and calling them one was tried and rejected — an offer and its revision are two documents, not two pages.
//
// It earns its place beyond that fixture too: nothing else in the demo seed has a second page, so preview
// paging, page reordering and the page-count column all had only single-page documents to work with.
//
// Same house style as sample-invoice / sample-offer: Acme Corp AG billing Muster Immobilien GmbH, Swiss format.

#set page(paper: "a4", margin: (x: 2cm, y: 2cm), numbering: "1 / 2")
#set text(font: "Helvetica Neue", size: 10pt, fallback: true)
#let accent = rgb("#1f4e79") // steel blue — a third identity, so the three demo documents read apart at a glance

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
    #text(size: 8pt, fill: luma(110))[VERTRAGSPARTNERIN / CONTRACT PARTY]
    #v(2pt)
    *Muster Immobilien GmbH* \
    z. Hd. Frau R. Bircher \
    Bahnhofstrasse 7 \
    6003 Luzern
  ],
  [
    #text(size: 22pt, weight: "bold", fill: accent)[WARTUNGS-\ VERTRAG]
    #v(4pt)
    #table(columns: 2, stroke: none, inset: (x: 0pt, y: 2pt), column-gutter: 12pt, align: (left, right),
      [Vertrags-Nr.], [*WV-2026-118*],
      [Datum], [20.03.2026],
      [Beginn], [01.04.2026],
      [Laufzeit], [2 Jahre],
    )
  ],
)

#v(20pt)
#line(length: 100%, stroke: 0.5pt + luma(180))
#v(12pt)

#text(size: 11pt, weight: "bold", fill: accent)[§ 1 Vertragsgegenstand]
#v(4pt)
Die Auftragnehmerin übernimmt die planmässige Wartung und Instandhaltung der in Anlage A aufgeführten
raumlufttechnischen Anlagen am Standort Bahnhofstrasse 7, 6003 Luzern. Massgebend sind die Angaben der
Hersteller sowie die Richtlinie SWKI VA104-01 in der bei Vertragsschluss gültigen Fassung.

#v(10pt)
#text(size: 11pt, weight: "bold", fill: accent)[§ 2 Leistungsumfang]
#v(4pt)
#table(columns: (auto, 1fr), stroke: none, inset: (x: 0pt, y: 3pt), column-gutter: 8pt, align: (left + top, left),
  [*2.1*], [Zwei Wartungsdurchgänge pro Kalenderjahr, jeweils im Frühjahr und im Herbst.],
  [*2.2*], [Sichtprüfung, Reinigung und Funktionskontrolle sämtlicher Bauteile gemäss Wartungsplan.],
  [*2.3*], [Wechsel der Filterelemente der Klassen ISO ePM1 55 % und ISO Coarse 60 % nach Bedarf, mindestens
            jedoch einmal jährlich.],
  [*2.4*], [Messung der Volumenströme und Druckdifferenzen; Dokumentation im Wartungsprotokoll, das der
            Auftraggeberin innert zehn Arbeitstagen nach jedem Durchgang zugestellt wird.],
  [*2.5*], [Führung eines Anlagenbuchs mit sämtlichen Eingriffen, Ersatzteilen und Messwerten.],
)

#v(10pt)
#text(size: 11pt, weight: "bold", fill: accent)[§ 3 Reaktionszeiten bei Störungen]
#v(4pt)
Störungsmeldungen werden werktags von 07:00 bis 17:00 Uhr entgegengenommen. Bei einem Betriebsunterbruch
erfolgt die Reaktion innert vier Stunden, in allen übrigen Fällen innert zweier Arbeitstage. Einsätze
ausserhalb der genannten Zeiten werden nach Aufwand zu den Ansätzen gemäss § 4.3 verrechnet.

#v(10pt)
#text(size: 11pt, weight: "bold", fill: accent)[§ 4 Vergütung]
#v(4pt)
#table(columns: (auto, 1fr, auto), stroke: none, inset: (x: 0pt, y: 3pt), column-gutter: 8pt, align: (left + top, left, right),
  [*4.1*], [Jahrespauschale für die Leistungen nach § 2], [CHF 1'240.00],
  [*4.2*], [Filterelemente und Verbrauchsmaterial], [nach Aufwand],
  [*4.3*], [Servicetechniker ausserhalb des Wartungsplans, pro Stunde], [CHF 145.00],
)
#v(4pt)
Sämtliche Beträge verstehen sich zuzüglich Mehrwertsteuer. Die Jahrespauschale wird jeweils im Voraus in
Rechnung gestellt und ist innert 30 Tagen netto zahlbar.

#pagebreak()

#text(size: 11pt, weight: "bold", fill: accent)[§ 5 Laufzeit und Kündigung]
#v(4pt)
Der Vertrag tritt am 01.04.2026 in Kraft und wird zunächst für zwei Jahre fest abgeschlossen. Er verlängert
sich stillschweigend um jeweils ein weiteres Jahr, sofern er nicht mit einer Frist von drei Monaten auf das
Ende der jeweiligen Laufzeit schriftlich gekündigt wird. Das Recht zur ausserordentlichen Kündigung aus
wichtigem Grund bleibt beiden Parteien vorbehalten.

#v(10pt)
#text(size: 11pt, weight: "bold", fill: accent)[§ 6 Haftung]
#v(4pt)
Die Auftragnehmerin haftet für Schäden aus der Verletzung vertraglicher Pflichten nach den Bestimmungen des
Obligationenrechts. Die Haftung für leichte Fahrlässigkeit ist auf den Betrag der Jahrespauschale beschränkt,
soweit dies gesetzlich zulässig ist. Für Schäden an Anlagenteilen, die älter als fünfzehn Jahre sind und für
die keine Ersatzteile mehr verfügbar sind, wird jede Haftung wegbedungen.

#v(10pt)
#text(size: 11pt, weight: "bold", fill: accent)[§ 7 Anlage A — erfasste Anlagen]
#v(4pt)
#table(columns: (auto, 1fr, auto, auto),
  stroke: (x, y) => if y == 0 { (bottom: 0.5pt + luma(150)) } else { none },
  inset: (x: 0pt, y: 4pt), column-gutter: 10pt, align: (left, left, left, right),
  [*Pos.*], [*Anlage*], [*Standort*], [*Baujahr*],
  [1], [Lüftungsgerät L-2, Zuluft 4'800 m³/h], [UG, Technikraum West], [2014],
  [2], [Lüftungsgerät L-3, Abluft 4'200 m³/h], [UG, Technikraum West], [2014],
  [3], [Dachventilator DV-1], [Dach, Achse C], [2018],
  [4], [Wärmerückgewinnung WRG-1, Rotationstauscher], [UG, Technikraum West], [2014],
)

#v(10pt)
#text(size: 11pt, weight: "bold", fill: accent)[§ 8 Schlussbestimmungen]
#v(4pt)
Änderungen und Ergänzungen dieses Vertrags bedürfen zu ihrer Gültigkeit der Schriftform; dies gilt auch für
den Verzicht auf das Schriftformerfordernis. Sollten einzelne Bestimmungen unwirksam sein oder werden, bleibt
die Wirksamkeit der übrigen Bestimmungen davon unberührt. Gerichtsstand ist Luzern; es gilt schweizerisches
Recht unter Ausschluss des Kollisionsrechts.

#v(30pt)
Luzern, 20. März 2026

#v(40pt)
#grid(columns: (1fr, 1fr), gutter: 30pt,
  [
    #line(length: 100%, stroke: 0.5pt + luma(120))
    #v(3pt)
    #text(size: 9pt)[Acme Corp AG \ M. Wyss, Leiter Service]
  ],
  [
    #line(length: 100%, stroke: 0.5pt + luma(120))
    #v(3pt)
    #text(size: 9pt)[Muster Immobilien GmbH \ R. Bircher, Liegenschaftsverwaltung]
  ],
)
