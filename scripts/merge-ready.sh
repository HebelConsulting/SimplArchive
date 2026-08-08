#!/usr/bin/env bash
#
# merge-ready.sh — is this PR actually safe to merge?
#
# `gh pr checks` answers "did anything fail?". That is NOT the same question as "was anything
# checked?", and since the heavy E2E tier became gated (it runs on pushes to `main`, or on a PR
# carrying the `e2e` label) the two answers diverge. A job skipped by its `if:` reports the bucket
# `skipping`, which reads as not-a-failure everywhere: `gh pr checks` says "0 failing", and GitHub
# treats a skipped job as SATISFYING a required status check.
#
# So an unverified branch presents as a wall of green, and it does so in the safe-looking direction.
# That is issue #418, from the near-miss on PR #415 — all six E2E legs skipped, "all checks
# complete, 0 failing", and the last real result for one of those legs had been red.
#
# This script asserts the E2E legs RAN. It expects as many passing `E2E (…)` checks as ci.yml's
# matrix declares, and treats `skipping` as unverified rather than as fine.
#
# Usage:
#   scripts/merge-ready.sh            # the PR for the current branch
#   scripts/merge-ready.sh 419        # a specific PR
#
# Exit codes:
#   0  ready to merge — everything ran, everything passed
#   1  not ready — something failed, is still running, or never ran
#   2  usage or environment problem (no gh, not a PR, could not read ci.yml)
#
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ci_yml="$repo_root/.github/workflows/ci.yml"

die() {
    printf '%s\n' "$*" >&2
    exit 2
}

command -v gh >/dev/null 2>&1 || die "merge-ready: the GitHub CLI (gh) is not installed."
command -v jq >/dev/null 2>&1 || die "merge-ready: jq is not installed."
[ -f "$ci_yml" ] || die "merge-ready: cannot find $ci_yml"

# How many E2E legs SHOULD there be? Read it from ci.yml's matrix rather than hard-coding a number
# here, so splitting or merging a leg cannot silently lower the bar this script enforces. If the
# parse ever breaks we abort loudly — a silent 0 would make every branch look verified, which is
# the exact failure mode this script exists to prevent.
expected="$(awk '/^      matrix:/ {in_matrix = 1} in_matrix && /^          - \{ name:/ {n++} /^    steps:/ {in_matrix = 0} END {print n + 0}' "$ci_yml")"
[ "$expected" -gt 0 ] || die "merge-ready: could not read the E2E matrix out of $ci_yml (got $expected legs).
The matrix formatting probably changed — fix the awk in this script rather than hard-coding a count."

pr="${1:-}"
if [ -z "$pr" ]; then
    pr="$(gh pr view --json number --jq .number 2>/dev/null)" ||
        die "merge-ready: no PR for the current branch. Pass a PR number explicitly."
fi

title="$(gh pr view "$pr" --json title --jq .title)"
printf 'PR #%s — %s\n\n' "$pr" "$title"

# `gh pr checks` exits non-zero when checks are failing or pending, so don't let -e kill us here.
checks="$(gh pr checks "$pr" --json name,bucket,link 2>/dev/null || true)"
[ -n "$checks" ] || die "merge-ready: could not read checks for PR #$pr."

count_bucket() { jq -r --arg b "$1" '[.[] | select(.bucket == $b)] | length' <<<"$checks"; }
e2e_in_bucket() {
    jq -r --arg b "$1" '[.[] | select(.name | startswith("E2E (")) | select(.bucket == $b)] | length' <<<"$checks"
}

e2e_pass="$(e2e_in_bucket pass)"
e2e_fail="$(e2e_in_bucket fail)"
e2e_skip="$(e2e_in_bucket skipping)"
e2e_pending="$(e2e_in_bucket pending)"

failing="$(count_bucket fail)"
pending="$(count_bucket pending)"

# Per-check listing, skips called out as their own state rather than folded into "not failing".
jq -r '.[] | "  \(if .bucket == "pass" then "✓" elif .bucket == "fail" then "✗" elif .bucket == "pending" then "…" else "⊘" end)  \(.name)  [\(.bucket)]"' <<<"$checks" | sort -k2

printf '\nE2E legs: %s/%s passed, %s failed, %s skipped, %s pending\n\n' \
    "$e2e_pass" "$expected" "$e2e_fail" "$e2e_skip" "$e2e_pending"

ready=0

if [ "$failing" -gt 0 ]; then
    printf '✗ NOT READY — %s check(s) failing.\n' "$failing"
    jq -r '.[] | select(.bucket == "fail") | "    \(.name)  \(.link)"' <<<"$checks"
    ready=1
fi

if [ "$pending" -gt 0 ]; then
    printf '… NOT READY — %s check(s) still running. Wait for them.\n' "$pending"
    ready=1
fi

if [ "$e2e_pass" -lt "$expected" ] && [ "$e2e_fail" -eq 0 ] && [ "$e2e_pending" -eq 0 ]; then
    cat <<EOF
✗ NOT READY — this branch is UNVERIFIED.

    Only $e2e_pass of $expected E2E legs passed — $e2e_skip skipped.
    A skipped check is not a passing check, however green the PR page looks.

    To verify it, BOTH of these, in this order:
      1. gh pr edit $pr --add-label e2e
      2. push a commit — 'labeled' only affects runs triggered afterwards,
         so labelling an already-finished run does nothing to that run.
EOF
    ready=1
fi

if [ "$ready" -eq 0 ]; then
    printf '✓ READY — all %s E2E legs ran and passed, nothing failing, nothing pending.\n' "$expected"
fi

exit "$ready"
