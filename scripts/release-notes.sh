#!/usr/bin/env bash
# The release notes' changelog section, generated from the PRIVATE repository's merged PRs (#1589, ADR 0889).
#
# Usage:  scripts/release-notes.sh <previous-tag> [<new-tag>]
#         e.g. scripts/release-notes.sh v0.36.0            # everything merged since v0.36.0, up to origin/main
#              scripts/release-notes.sh v0.35.0 v0.36.0    # reproduce a past release's list
#         scripts/release-notes.sh --check <file>          # run the same leak gates over a file — the CURATED
#                                                          # section is published too, and is hand-written
#         scripts/release-notes.sh --rephrase <tsv> <previous-tag> [<new-tag>]
#                                                          # replace refused titles: one "exact title<TAB>new
#                                                          # wording" per line; the new wording is re-checked
#
# WHY NOT `gh release create --generate-notes` (ADR 0096's mechanism): the PUBLIC repository's history is filtered
# snapshots, `Sync from private @ <sha>`, so between v0.35.0 and v0.36.0 GitHub had 44 sync commits and no PR
# titles, and produced a one-line body. The PRs exist only here.
#
# THE RANGE IS EXACT, not a date guess. Each public tag sits on a `Sync from private @ <sha>` commit, so the tag
# names the private commit it was cut from; the PRs are the first-parent merge commits between the two. A date
# window would include or drop a PR merged in the hours between a merge and its publish.
#
# TITLES ONLY — no private PR numbers or links. The notes are published on the public repository, whose readers
# cannot open a private PR, so a `#1586` would be a dead reference to a repository they cannot see. Issue
# numbers INSIDE a title are kept but escaped (`\#`), see the output section.
#
# REFUSES TO PRINT, rather than rewriting, when a title would leak (exit 3, the offending titles on stderr):
#   * the withheld-product-name gate the publish itself enforces (publish/publish-public.sh — its two *_PATTERN
#     assignments; read by SHAPE, because this file is published and must not name what that gate withholds),
#   * the brand gate's patterns (.github/workflows/ci.yml — PATTERN and PROSE_PATTERN, ADR 0610),
#   * a path the public mirror withholds (CLAUDE.md, docs/, tools/, publish/, .claude/).
# The patterns are READ from those files, never copied here, so there is one list each. Rephrase the flagged title
# in the release body by hand — a script that silently dropped or rewrote lines would publish a changelog nobody
# chose. Exit 2: could not determine the range or read a pattern (nothing is printed then either).
set -euo pipefail

PRIVATE_REPO=${PRIVATE_REPO:-HebelConsulting/SimplArchivePrivate}
PUBLIC_REPO=${PUBLIC_REPO:-HebelConsulting/SimplArchive}
ROOT="$(git rev-parse --show-toplevel)"

die() { echo "release-notes: $*" >&2; exit 2; }

REPHRASE_FILE=""
if [ "${1:-}" = "--rephrase" ]; then
  [ $# -ge 3 ] && [ -f "$2" ] || die "usage: $0 --rephrase <tsv> <previous-tag> [<new-tag>]"
  REPHRASE_FILE=$2
  shift 2
fi
[ $# -ge 1 ] && [ $# -le 2 ] || die "usage: $0 [--rephrase <tsv>] <previous-tag> [<new-tag>]  |  $0 --check <file>"
CHECK_FILE=""
if [ "$1" = "--check" ]; then
  [ $# -eq 2 ] && [ -f "$2" ] || die "usage: $0 --check <file>"
  CHECK_FILE=$2
fi
PREVIOUS_TAG=$1
NEW_TAG=${2:-}

# --- the patterns, read from their one home each ------------------------------------------------------------------
# Single-quoted assignments on one line in both files; parsed rather than sourced, because sourcing publish-public.sh
# would run a publish and ci.yml is not a shell file.
single_quoted() { sed -n "s/^[[:space:]]*$1='\(.*\)'[[:space:]]*\$/\1/p" "$2" | head -1; }

# The publish's product-name gate is two assignments, `<NAME>_PATTERN` and `<NAME>_IDENT_PATTERN`. Matched by that
# shape and required to be EXACTLY one each — a third pattern there would be ambiguous, and is refused rather than
# guessed at.
PUBLISH_SCRIPT="$ROOT/publish/publish-public.sh"
pattern_by_shape() {
  local found
  found=$(sed -n "s/^$1='\(.*\)'[[:space:]]*\$/\1/p" "$PUBLISH_SCRIPT")
  [ "$(printf '%s\n' "$found" | sed '/^$/d' | wc -l | tr -d ' ')" = 1 ] \
    || die "expected exactly one assignment shaped $1 in $PUBLISH_SCRIPT — the gate changed shape; update this parse"
  printf '%s' "$found"
}
PRODUCT_IDENT_PATTERN=$(pattern_by_shape '[A-Z][A-Z]*_IDENT_PATTERN')
PRODUCT_PATTERN=$(pattern_by_shape '[A-Z][A-Z]*[A-Z]_PATTERN')
BRAND_PATTERN=$(single_quoted PATTERN "$ROOT/.github/workflows/ci.yml")
PROSE_PATTERN=$(single_quoted PROSE_PATTERN "$ROOT/.github/workflows/ci.yml")
# The withheld set, as the forms a TITLE would name it in (publish-public.sh's WITHHELD, path-shaped so that the word
# "docs" in "docs(manual): …" is not a hit — only a path into the withheld tree is).
WITHHELD_PATTERN='(CLAUDE\.md|(^|[^[:alnum:]_])(docs|tools|publish|\.claude)/)'

# Anti-vacuous: a pattern that failed to parse would make every check below pass while checking nothing.
for name in PRODUCT_PATTERN PRODUCT_IDENT_PATTERN BRAND_PATTERN PROSE_PATTERN; do
  [ -n "${!name}" ] || die "could not read $name — the gate it comes from moved; fix the parse, never skip the check"
done

# --- the gates -----------------------------------------------------------------------------------------------------
# The match is CAPTURED and its emptiness tested — never grep's quiet mode. Under pipefail a quiet grep that exits
# at its first match can turn a MATCH into a failure, which in a leak check is the dangerous direction; capturing
# the output has no early exit at all (GrepQUnderPipefailTests holds every script to this).
matches()   { [ -n "$(grep -E -- "$1" <<<"$2" || true)" ]; }     # case-sensitive
matches_i() { [ -n "$(grep -iE -- "$1" <<<"$2" || true)" ]; }    # case-insensitive
REFUSED=()
check() {
  local line=$1 why=""
  if matches_i "$PRODUCT_PATTERN" "$line" || matches "$PRODUCT_IDENT_PATTERN" "$line"; then why="names the withheld product"
  elif matches_i "$BRAND_PATTERN" "$line" || matches_i "$PROSE_PATTERN" "$line"; then why="brand name (ADR 0610)"
  elif matches "$WITHHELD_PATTERN" "$line"; then why="names a path the public mirror withholds"
  fi
  [ -z "$why" ] || REFUSED+=("$why: $line")
}
report_refused() {
  [ "${#REFUSED[@]}" -gt 0 ] || return 0
  {
    echo "release-notes: REFUSED — ${#REFUSED[@]} line(s) must not be published as written. Rephrase them by hand"
    echo "(say what changed without the name) — for titles, with --rephrase <tsv>; nothing was printed."
    printf '  %s\n' "${REFUSED[@]}"
  } >&2
  exit 3
}

if [ -n "$CHECK_FILE" ]; then
  while IFS= read -r line || [ -n "$line" ]; do check "$line"; done <"$CHECK_FILE"
  report_refused
  echo "release-notes: $CHECK_FILE is clean" >&2
  exit 0
fi

# --- the range ---------------------------------------------------------------------------------------------------
private_sha_of() {
  local subject
  subject=$(gh api "repos/$PUBLIC_REPO/commits/$1" --jq '.commit.message | split("\n")[0]') \
    || die "no tag $1 on $PUBLIC_REPO"
  case "$subject" in
    "Sync from private @ "*) echo "${subject#Sync from private @ }" ;;
    *) die "$1 is on '$subject', not a 'Sync from private @ <sha>' commit — cannot map it to a private commit" ;;
  esac
}

git fetch --quiet origin main || die "could not fetch origin/main"
FROM=$(private_sha_of "$PREVIOUS_TAG")
if [ -n "$NEW_TAG" ]; then TO=$(private_sha_of "$NEW_TAG"); else TO=$(git rev-parse origin/main); fi
git cat-file -e "$FROM^{commit}" 2>/dev/null || die "private commit $FROM ($PREVIOUS_TAG) is not in this clone"
git cat-file -e "$TO^{commit}" 2>/dev/null || die "private commit $TO is not in this clone"

# The PR numbers, oldest first, from the merge commits git itself wrote. A read loop rather than `mapfile`, which
# macOS's stock bash 3.2 does not have — and this runs on the releaser's machine.
NUMBERS=()
while IFS= read -r n; do NUMBERS+=("$n"); done < <(git log --first-parent --merges --reverse --format='%s' "$FROM..$TO" \
  | sed -n 's/^Merge pull request #\([0-9][0-9]*\) from .*/\1/p')
[ "${#NUMBERS[@]}" -gt 0 ] || { echo "release-notes: no PRs merged between $PREVIOUS_TAG and ${NEW_TAG:-origin/main}" >&2; exit 0; }

# Titles in ONE call (the query #1589 names), then kept only for the exact set above.
SINCE=$(git log -1 --format='%cs' "$FROM")
TITLES_JSON=$(gh pr list --repo "$PRIVATE_REPO" --state merged --search "merged:>=$SINCE" --limit 500 \
  --json number,title,author) || die "could not list merged PRs on $PRIVATE_REPO"

LINES=()
DEPENDENCIES=()
for n in "${NUMBERS[@]}"; do
  row=$(jq -r --argjson n "$n" '.[] | select(.number == $n) | [.author.login, .title] | @tsv' <<<"$TITLES_JSON")
  [ -n "$row" ] || row=$(gh pr view "$n" --repo "$PRIVATE_REPO" --json author,title --jq '[.author.login, .title] | @tsv')
  author=${row%%$'\t'*}
  title=${row#*$'\t'}
  # A human's explicit replacement for a refused title — matched EXACTLY, never by pattern, so a rephrase can only
  # touch the one line it names. The replacement goes through the same gates below as everything else.
  if [ -n "$REPHRASE_FILE" ]; then
    replacement=$(awk -F'\t' -v t="$title" '$1 == t { print $2; exit }' "$REPHRASE_FILE")
    [ -z "$replacement" ] || title=$replacement
  fi
  case "$author" in
    app/dependabot|dependabot*) DEPENDENCIES+=("$title") ;;
    *) LINES+=("$title") ;;
  esac
done

for t in "${LINES[@]}" ${DEPENDENCIES[@]+"${DEPENDENCIES[@]}"}; do check "$t"; done
report_refused

# --- the output: markdown for the release body, under the curated section ---------------------------------------
# Titles carry PRIVATE issue references ("(#1380)"). On the public release page GitHub would auto-link each to the
# PUBLIC repository's #1380 — a different issue or none, so a WRONG link rather than a dead one. `\#` renders the
# same text without the link. The text is otherwise verbatim.
bullet() { printf -- '- %s\n' "${1//#/\\#}"; }

echo "## Every change in this release"
echo
for t in "${LINES[@]}"; do bullet "$t"; done
if [ "${#DEPENDENCIES[@]}" -gt 0 ]; then
  echo
  echo "### Dependencies"
  echo
  for t in "${DEPENDENCIES[@]}"; do bullet "$t"; done
fi
echo
echo "**Full Changelog**: https://github.com/$PUBLIC_REPO/compare/$PREVIOUS_TAG...${NEW_TAG:-main}"
