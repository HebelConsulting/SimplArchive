#!/usr/bin/env bash
#
# Reports whether a repository seeded from docs/SeedCLAUDE.md still carries its standing principles
# (issue #1469).
#
# SeedClaudeLockstepTests guards one direction — every principle in this repo's CLAUDE.md is either carried
# into the seed or marked as a deliberate omission. Nothing guarded the other: the seed moves and a repository
# initialised from it silently does not. The principles exist because each was paid for by a real failure, so
# a repository that never received one is free to repeat it.
#
# COMPARE TITLES, NEVER COUNTS. The issue's own table was built from counts and got flight school wrong: it
# has as many titles as the seed and looked in step, while carrying one of its own and missing one of the
# seed's. A count cannot see a substitution.
#
# Exit 0 = every seeded repository carries every seed principle. Exit 1 = drift, or a repository nobody has
# classified. Needs an authenticated `gh` (GH_TOKEN with read access to the private siblings).
set -uo pipefail

ORG=HebelConsulting
SEED="$(cd "$(dirname "$0")/.." && pwd)/docs/SeedCLAUDE.md"

# The repositories initialised FROM the seed. A repo arrives here by a decision, not by discovery: adding it
# is the moment somebody decides it is seeded, and discovery would quietly start measuring one that never was.
SEEDED=(SimplArchiveEncryption SimplArchiveEncryptionService SimplArchiveFlightSchool)

# Deliberately NOT measured, each for its own reason — recorded here because an unexplained exclusion is
# indistinguishable from an oversight, and counting these would make the problem look twice its size.
#   CAManagement      predates the seed entirely and never mentions it.
#   SimplArchiveDeck  a deliberately bespoke CLAUDE.md in a different shape (content lives once, bundled
#                     fonts only, measure text through LibreOffice).
#   SimplArchive      the public mirror of THIS repository; CLAUDE.md is withheld from it by ADR 0484.
EXCLUDED=(CAManagement SimplArchiveDeck SimplArchive SimplArchivePrivate)

# A title, reduced to what a reader would call "the same principle": the prose, without the ADR or issue
# pointer a sibling may legitimately drop, case-folded. Keeps an exact-looking comparison from failing on
# punctuation while still refusing a substitution.
normalise() {
  sed -E 's/^\*\*Standing (principle|convention) — //; s/ *\(`?(docs\/adr\/|ADR )[^)]*\)//g; s/[`*]//g' \
    | tr '[:upper:]' '[:lower:]' | sed -E 's/[[:space:]]+$//' | sort -u
}

titles_of_file() { grep -o '^\*\*Standing \(principle\|convention\) — [^:]*' "$1" | normalise; }

# One file, not a clone: the whole repository to read one Markdown file is minutes of CI for nothing.
claude_md_of_repo() {
  gh api "repos/$ORG/$1/contents/CLAUDE.md" --jq '.content' 2>/dev/null | base64 -d 2>/dev/null
}

if [[ ! -f "$SEED" ]]; then
  echo "cannot find the seed at $SEED" >&2
  exit 2
fi

titles_of_file "$SEED" > /tmp/seed-titles.$$
seed_count=$(wc -l < /tmp/seed-titles.$$ | tr -d ' ')
echo "docs/SeedCLAUDE.md carries $seed_count standing principles."
echo

drift=0

for repo in "${SEEDED[@]}"; do
  if ! claude_md_of_repo "$repo" > "/tmp/claude-$repo.$$"; then
    echo "✗ $repo — could not read its CLAUDE.md (missing, or the token cannot see this repository)"
    drift=1
    continue
  fi

  titles_of_file "/tmp/claude-$repo.$$" > "/tmp/titles-$repo.$$"

  missing=$(comm -23 /tmp/seed-titles.$$ "/tmp/titles-$repo.$$")
  own=$(comm -13 /tmp/seed-titles.$$ "/tmp/titles-$repo.$$" | wc -l | tr -d ' ')

  if [[ -n "$missing" ]]; then
    echo "✗ $repo — missing $(printf '%s\n' "$missing" | wc -l | tr -d ' ') of $seed_count (and carries $own of its own):"
    printf '      %s\n' "$missing"
    drift=1
  else
    echo "✓ $repo — carries all $seed_count (plus $own of its own)"
  fi
done

echo
# The classification itself can drift: a NEW repository seeded from the extract, which nobody adds to either
# list above, is exactly the case this script exists to catch and the one it would otherwise be blind to.
for repo in $(gh repo list "$ORG" --limit 100 --json name --jq '.[].name' | sort); do
  known=0
  for k in "${SEEDED[@]}" "${EXCLUDED[@]}"; do [[ "$repo" == "$k" ]] && known=1; done
  [[ $known -eq 1 ]] && continue

  body=$(claude_md_of_repo "$repo")
  mentions=$(printf '%s' "$body" | grep -c 'SeedCLAUDE' || true)
  if [[ "$mentions" -gt 0 ]]; then
    echo "✗ $repo names SeedCLAUDE.md but is in neither list — classify it as seeded or excluded (with a reason)"
    drift=1
  fi
done

rm -f /tmp/seed-titles.$$ /tmp/claude-*.$$ /tmp/titles-*.$$

if [[ $drift -eq 0 ]]; then
  echo "No drift: every seeded repository carries every seed principle."
  exit 0
fi

echo
echo "Drift above. A missing principle is closed by adding it to that repository's CLAUDE.md in its"
echo "generalised seed form — not by relaxing this script."
exit 1
