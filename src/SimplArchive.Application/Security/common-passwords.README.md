# `common-passwords.txt`

The 10,000 most common passwords, used by `PasswordPolicy` to refuse a chosen password that is already on a
breach list (#849, ADR 0065's other half).

- **Source**: [SecLists](https://github.com/danielmiessler/SecLists) —
  `Passwords/Common-Credentials/10k-most-common.txt`, fetched 2026-10-02.
- **Licence**: MIT (Copyright (c) 2018 Daniel Miessler), which the project's licence policy permits.

## Why this file is checked against a NORMALISED password, and why that matters

Measured when it was added: **only 10 of its 10,001 entries are 12 characters or longer**, and the minimum
length is 12. Looked up verbatim, this list would therefore refuse almost nothing the length rule does not
already refuse, and the ten exceptions are obscenities rather than anything a person would choose for a work
account. A control that overlaps another one completely is not defence in depth — it is decoration that looks
like a control.

So the password is **normalised before lookup** — case folded, leet substitutions undone, trailing digits and
punctuation removed — which is what turns this list into something that bites: `Summer2026!`, `P@ssw0rd2026`
and `Basketball2026` all reduce to entries in it. That is the shape people actually choose when told to pick
twelve characters with a number in it.

## It contains obscenities, deliberately

It is a list of what people really use. It is data, never displayed, and never logged.
