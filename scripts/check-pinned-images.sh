#!/usr/bin/env bash
# Reports which compose-pinned third-party images have a newer release, and exits non-zero when any do.
#
# WHY THIS EXISTS (#1214). Dependabot's `docker` ecosystem reads Dockerfiles and Kubernetes YAML. It does NOT
# read docker-compose files — the configuration claimed a weekly cadence for them and the only docker bump it
# has ever opened was for a directory with a real Dockerfile. So the images the dev/demo stack and the PUBLIC
# KIOSK run on — Postgres, SeaweedFS, OpenSearch, Tika, Gotenberg, Mailpit, Valkey, pgAdmin, OpenBao, Caddy —
# were pinned deliberately (ADR 0507, so a floating tag cannot shift under a deployment) and then watched by
# nobody. A pin without a bump path is how a pin becomes an unpatched version.
#
# WHAT IT COMPARES, and what it refuses to. Within the SAME MAJOR only. A major bump is a decision about
# breaking changes and belongs to a human reading release notes; a patch or minor inside the major the project
# already chose is the thing nobody should have to remember. So valkey 8.x will never be reported as "behind"
# because 9.0 exists — that is deliberate, and saying so here is cheaper than arguing about it later.
#
# A NETWORK FAILURE IS NOT "UP TO DATE". Every lookup that cannot be completed is reported as UNKNOWN and makes
# the script exit non-zero, because the alternative — a registry outage reading as a clean bill of health — is
# exactly the shape of failure this project keeps paying for. Docker Hub returned 500s on the day this was
# written, which is what makes the distinction concrete rather than theoretical.
#
# Digest pins (@sha256:) are skipped: they are already exact, and resolving "is there a newer digest" needs a
# tag to compare against, which a digest pin deliberately does not carry.
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# The dev stack's versions live in images.env (the source of truth); docker-compose.yaml substitutes from it,
# so reading the compose file would now yield "${POSTGRES_TAG:-16.15-alpine}" rather than a version. The kiosk
# still carries literals, because it lags at rollout on purpose.
files=("$repo_root/images.env")
[[ -f "$repo_root/tools/kiosk/docker-compose.yml" ]] && files+=("$repo_root/tools/kiosk/docker-compose.yml")

# images.env maps NAME=tag; every other file names `image: repo:tag`. Turn the first into the second so one
# loop reads both, using the repository each variable stands for.
repo_for() {
  case "$1" in
    POSTGRES_TAG) echo postgres ;;
    OPENSEARCH_TAG) echo opensearchproject/opensearch ;;
    TIKA_TAG) echo apache/tika ;;
    GOTENBERG_TAG) echo gotenberg/gotenberg ;;
    VALKEY_TAG) echo valkey/valkey ;;
    MAILPIT_TAG) echo axllent/mailpit ;;
    PGADMIN_TAG) echo dpage/pgadmin4 ;;
    OPENBAO_TAG) echo openbao/openbao ;;
    CADDY_VERSION) echo caddy ;;
    AWSCLI_TAG) echo amazon/aws-cli ;;
    *) echo "" ;;
  esac
}

# Collected PER FILE, not merged. The dev stack and the kiosk pin the same images and drift apart on purpose
# — the kiosk is the outward-facing demo and is bumped at rollout, not with a dev-stack change. Merging them
# produced output listing one image twice, "8.34.0 BEHIND" beside "8.37 up to date", with nothing to say which
# stack was which. Reporting by file turns that from a confusing duplicate into the actual finding: which
# stack is behind.

behind=0
unknown=0
for file in "${files[@]}"; do
  echo
  echo "── ${file#$repo_root/}"
  printf '%-34s %-22s %s\n' "IMAGE" "PINNED" "STATUS"

  pins=()
  if [[ "$file" == *images.env ]]; then
    while IFS= read -r line; do
      name="${line%%=*}"; tag="${line#*=}"
      repo="$(repo_for "$name")"
      [[ -n "$repo" && -n "$tag" ]] && pins+=("$repo:$tag")
    done < <(grep -E '^[A-Z_]+=' "$file" | grep -v '_DIGEST=')
  else
    while IFS= read -r line; do
      [[ -n "$line" ]] && pins+=("$line")
    done < <(
      grep -hE '^[[:space:]]+image:' "$file" \
        | sed -E 's/^[[:space:]]*image:[[:space:]]*//' \
        | grep -v '@sha256:' \
        | grep -v '^ghcr\.io/hebelconsulting/' \
        | sort -u
    )
  fi

  for pin in ${pins[@]+"${pins[@]}"}; do
  repo="${pin%:*}"
  tag="${pin##*:}"
  case "$repo" in
    */*) ns="repositories/$repo" ;;
    *)   ns="repositories/library/$repo" ;;
  esac

  newest=$(curl -s --max-time 20 "https://hub.docker.com/v2/${ns}/tags?page_size=100&ordering=last_updated" \
    | TAG="$tag" python3 -c '
import sys, json, os, re

# A leading "v" belongs to some tag styles (mailpit); it is not part of the version.
def parse(t):
    m = re.match(r"^v?(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?(.*)$", t)
    if not m:
        return None
    a, b, c, d, suffix = m.group(1), m.group(2), m.group(3), m.group(4), m.group(5)
    return (int(a), int(b), int(c or 0), int(d or 0), suffix)

want = parse(os.environ["TAG"])
if want is None:
    print("SKIP"); raise SystemExit

try:
    data = json.load(sys.stdin)
except Exception:
    print("UNKNOWN"); raise SystemExit

# Carry the REAL tag name alongside the parsed version. Reconstructing a version string from the parsed
# numbers invented tags that do not exist — "16.15.0-alpine" when the published tag is "16.15-alpine" — which
# would send somebody to a pin they cannot pull. Report what the registry actually calls it.
best, best_name = want, None
for t in data.get("results", []):
    name = t.get("name", "")
    got = parse(name)
    # same MAJOR and same suffix (-alpine stays -alpine, -full stays -full)
    if got and got[0] == want[0] and got[4] == want[4] and got > best:
        best, best_name = got, name

print("CURRENT" if best_name is None else best_name)
' 2>/dev/null)

  case "${newest:-UNKNOWN}" in
    CURRENT) printf '%-34s %-22s up to date\n' "$repo" "$tag" ;;
    SKIP)    printf '%-34s %-22s not a version tag — skipped\n' "$repo" "$tag" ;;
    UNKNOWN|"") printf '%-34s %-22s UNKNOWN (lookup failed — NOT a pass)\n' "$repo" "$tag"; unknown=$((unknown+1)) ;;
      *)       printf '%-34s %-22s BEHIND: %s available\n' "$repo" "$tag" "$newest"; behind=$((behind+1)) ;;
    esac
  done
done

echo
if (( behind == 0 && unknown == 0 )); then
  echo "All compose-pinned images are current within their major."
  exit 0
fi
(( behind > 0 ))  && echo "$behind pin(s) behind. The kiosk is bumped at ROLLOUT (docs/deploy/release.md), not with a dev-stack change."
(( unknown > 0 )) && echo "$unknown lookup(s) failed. A registry outage is not evidence that a pin is current."
exit 1
