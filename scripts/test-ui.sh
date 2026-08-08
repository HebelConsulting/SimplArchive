#!/usr/bin/env bash
#
# test-ui.sh — run the web UI end-to-end suite the way CI does.
#
# Use this instead of `dotnet test tests/SimplArchive.UiEndToEndTests`.
#
# WHY THIS EXISTS (issue #420): run as ONE process, the 140-test suite is unreliable — something
# accumulates across a ~20-minute run (one fixture, one browser, one API subprocess) and tests start
# timing out at login. Measured on one machine, same day, same commit:
#
#     one process           3, 2, 3 failures — and 8 on a clean tree with no change at all;  ~21 min
#     four legs, -j 1       139 / 140; the one failure re-ran green twice in isolation;      ~21 min
#     four legs, -j 2       140 / 140;                                                        ~6 min
#
# So this is not a trade of speed against reliability — it is better at both.
#
# A signal that gets WORSE when you remove the change under test is not measuring the change. That
# cost most of a session on #413 before a baseline run exposed it.
#
# CI never sees this because CI splits the suite into four legs, each its own process on its own
# runner. This script reproduces that locally. The legs are read out of ci.yml rather than hard-coded,
# so re-balancing the matrix cannot silently leave this script running a different set.
#
# The underlying accumulation is NOT fixed — #420 stays open for it.
#
# Usage:
#   scripts/test-ui.sh              # all legs, default parallelism
#   scripts/test-ui.sh -j 1         # sequential (slowest, lightest on RAM)
#   scripts/test-ui.sh -j 4         # all four at once (fastest, heaviest)
#   scripts/test-ui.sh --no-build   # skip the build step
#
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ci_yml="$repo_root/.github/workflows/ci.yml"

# Each leg starts its OWN Testcontainers fleet (Postgres + SeaweedFS + OpenSearch + Tika + Gotenberg)
# plus a Chrome and an API subprocess. CI affords four at once because it puts each on a separate
# 7 GB runner; one laptop does not, and starving them recreates the very contention this script exists
# to avoid. Two is the measured compromise — override with -j when you know your headroom.
jobs=2
build=1

while [ $# -gt 0 ]; do
    case "$1" in
        -j | --jobs)
            jobs="${2:-}"
            shift 2
            ;;
        --no-build)
            build=0
            shift
            ;;
        -h | --help)
            sed -n '2,32p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        *)
            printf 'test-ui: unknown argument %s (try --help)\n' "$1" >&2
            exit 2
            ;;
    esac
done

case "$jobs" in
    '' | *[!0-9]*) printf 'test-ui: --jobs needs a positive integer\n' >&2; exit 2 ;;
esac
[ "$jobs" -ge 1 ] || { printf 'test-ui: --jobs needs a positive integer\n' >&2; exit 2; }

[ -f "$ci_yml" ] || { printf 'test-ui: cannot find %s\n' "$ci_yml" >&2; exit 2; }

# Pull the UiEndToEndTests legs' --filter expressions straight out of the CI matrix. Aborts loudly on a
# parse failure: silently running zero legs would report success having tested nothing, which is the
# same "absence reads as a pass" trap as issue #418.
# Read with a loop rather than `mapfile`: macOS ships bash 3.2, and no other script here needs bash 4.
filters=()
while IFS= read -r leg; do
    [ -n "$leg" ] && filters+=("$leg")
done < <(grep -F 'project: UiEndToEndTests' "$ci_yml" | sed -n "s/.*--filter \"\([^\"]*\)\".*/\1/p")

if [ "${#filters[@]}" -eq 0 ]; then
    printf 'test-ui: could not read the UiEndToEndTests legs out of %s.\n' "$ci_yml" >&2
    printf 'The matrix formatting probably changed — fix the parse here rather than hard-coding the legs.\n' >&2
    exit 2
fi

printf '==> %s legs from ci.yml, %s at a time\n\n' "${#filters[@]}" "$jobs"

if [ "$build" -eq 1 ]; then
    printf '==> Building\n'
    dotnet build "$repo_root/tests/SimplArchive.UiEndToEndTests" -c Debug || exit 1
    printf '\n'
fi

logdir="$(mktemp -d)"
trap 'rm -rf "$logdir"' EXIT

run_leg() {
    local i="$1" filter="$2"
    dotnet test "$repo_root/tests/SimplArchive.UiEndToEndTests" --no-build -c Debug --filter "$filter" \
        > "$logdir/leg-$i.log" 2>&1
    printf '%s' "$?" > "$logdir/leg-$i.rc"
}

# Batches of `jobs`, waiting for each batch to finish. `wait -n` (start the next as soon as any one
# finishes) would be tighter, but it needs bash 4.3 and macOS ships 3.2. With four legs the difference
# is a minute at most, and correctness on the shell people actually have beats it.
started=$(date +%s)
i=0
total=${#filters[@]}
while [ "$i" -lt "$total" ]; do
    pids=""
    n=0
    while [ "$n" -lt "$jobs" ] && [ "$i" -lt "$total" ]; do
        run_leg "$i" "${filters[$i]}" &
        pids="$pids $!"
        i=$((i + 1))
        n=$((n + 1))
    done
    for p in $pids; do wait "$p"; done
done

printf '%-46s %-9s %s\n' "LEG" "RESULT" "DETAIL"
failed=0
for i in "${!filters[@]}"; do
    rc="$(cat "$logdir/leg-$i.rc" 2>/dev/null || echo 1)"
    summary="$(grep -Eo '(Passed|Failed)! *- *Failed: *[0-9]+, *Passed: *[0-9]+[^-]*' "$logdir/leg-$i.log" | tail -1)"
    counts="$(printf '%s' "$summary" | sed -E 's/.*Failed: *([0-9]+), *Passed: *([0-9]+).*/\2 passed, \1 failed/')"
    if [ "$rc" = "0" ]; then
        printf '%-46s %-9s %s\n' "${filters[$i]}" "PASS" "$counts"
    else
        failed=1
        printf '%-46s %-9s %s\n' "${filters[$i]}" "FAIL" "$counts"
        grep -E '\[FAIL\]' "$logdir/leg-$i.log" | sed -E 's/^\[[^]]*\] *//; s/^/      /' | head -20
    fi
done

printf '\nElapsed: %ss\n' "$(( $(date +%s) - started ))"

if [ "$failed" -ne 0 ]; then
    cat <<EOF

Before assuming a regression: this suite still has residual flakiness (#420). Re-run the failing
test on its own — if it passes in isolation it is noise, and the useful comparison is a baseline
run with your change stashed, not another run with it.
EOF
    exit 1
fi

printf 'All legs green.\n'
