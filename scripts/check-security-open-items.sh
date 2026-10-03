#!/usr/bin/env bash
# Is every line of the manual's "What is still open" still OPEN? (issue #1592)
#
# The security appendix's §20.4 lists what the product has NOT done yet. It went on saying images were unsigned in
# the release that signed them — five of its seven lines false, two half-true — because nothing ever asked whether
# a sentence was still accurate: the manual gate asks whether the document compiles. So each line names the issue
# that tracks it (ManualOpenItemsCiteAnIssueTests enforces that), and this asks GitHub whether that issue is still
# open. A CLOSED issue under a line that still says "open" is the decay this exists to catch.
#
# Exit codes, kept DIFFERENT on purpose (verify-flight-school-seed.sh has the story of collapsing them):
#   0  every cited issue is open
#   1  a cited issue is CLOSED — the line naming it is stale; rewrite it (and move the control to §20.3)
#   2  could not tell — no bullets found, gh failed, or an issue could not be read. Not a finding about the manual.
set -uo pipefail

MANUAL="${1:-manual/manual.typ}"
[ -f "$MANUAL" ] || { echo "::error::$MANUAL not found"; exit 2; }

# The section, from its heading to the next same-level heading.
section="$(awk '/^== What is still open/{p=1; next} p && /^== /{exit} p' "$MANUAL")"
if [ -z "$section" ]; then
  echo "::error::No '== What is still open' section in $MANUAL — the heading changed and this check stopped seeing it."
  exit 2
fi

# One bullet per line: a line starting "- " begins one, indented lines continue it.
bullets="$(printf '%s\n' "$section" | awk '/^- /{if (b) print b; b=$0; next} /^  /{if (b) b=b" "$0; next} {if (b) print b; b=""} END{if (b) print b}')"
if [ -z "$bullets" ]; then
  echo "::error::'What is still open' has no bullets. If nothing is open, say so in prose and delete this check — do not let it pass on an empty list."
  exit 2
fi

stale=0
unknown=0
while IFS= read -r bullet; do
  # Typst escapes '#', so the manual writes \#1578; accept either spelling.
  numbers="$(printf '%s\n' "$bullet" | grep -oE '\\?#[0-9]+' | tr -d '\\#' | sort -u)"
  title="$(printf '%s\n' "$bullet" | sed -E 's/^- \*([^*]+)\*.*/\1/')"
  if [ -z "$numbers" ]; then
    echo "::error::'$title' names no issue — every line must cite the issue that tracks it."
    stale=1
    continue
  fi
  for n in $numbers; do
    # Captured before it is tested: under pipefail a short-circuiting reader can turn a match into a failure.
    if ! state="$(gh issue view "$n" --json state --jq .state 2>/dev/null)" || [ -z "$state" ]; then
      echo "::warning::could not read issue #$n (cited by '$title')"
      unknown=1
      continue
    fi
    if [ "$state" = "OPEN" ]; then
      echo "ok     #$n  $title"
    else
      echo "::error::#$n is $state, but the manual still lists '$title' as open. Rewrite the line, and move the control into the delivered tables if it shipped."
      stale=1
    fi
  done
done <<< "$bullets"

[ "$stale" -eq 0 ] || exit 1
[ "$unknown" -eq 0 ] || exit 2
echo "Every line of 'What is still open' cites an open issue."
