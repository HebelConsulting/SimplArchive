#!/bin/sh
# Install the industry modules this deployment pins, and refuse to finish if it cannot (ADR 0799).
#
# Runs as a one-shot BEFORE the API starts — a fourth init step beside db-init, storage-init and openbao-init,
# and an initContainer in the Helm chart. The application is never given the package credential and never
# resolves anything at startup: a feed outage must be a failed init step, not a boot failure of the whole app.
#
# WHY IT REFUSES RATHER THAN DEGRADES. A tenant holding an active module licence whose module did not install
# loses its controllers, masks and transitions, and every module route answers 404 MODULE_NOT_ACTIVE — which
# reads as "the feature was never built" rather than "it is not installed". That is exactly the invisible
# failure #1242 was, so the fix for it must not reintroduce it one layer down.
#
# TOOLING: a .nupkg is a ZIP, so this needs no NuGet tooling and no SDK image — plain alpine is enough. But
# `unzip` and `sha512sum` are NOT on alpine's PATH (verified, 3.22.5); they exist only as busybox applets, so
# every call below is explicit. `wget` IS on PATH and takes --header, which is how the bearer token is sent.
set -eu

MODULES_ENV="${MODULES_ENV:-/modules.env}"
TARGET="${TARGET:-/modules}"
FEED="${SA_MODULE_FEED:-https://nuget.pkg.github.com/HebelConsulting}"
SA_MODULES="${SA_MODULES:-}"

die() { echo "modules-init: FAILED — $*" >&2; exit 1; }

# WHICH modules this deployment installs is the deployment's choice; modules.env only says which VERSION.
# Empty is the default and is a legitimate answer: the dev stack runs core-only, so `docker compose up --build`
# needs no package credential and keeps working exactly as the README documents.
if [ -z "$SA_MODULES" ]; then
    echo "modules-init: SA_MODULES is empty — this deployment installs no module. Nothing to do."
    exit 0
fi

[ -f "$MODULES_ENV" ] || die "$MODULES_ENV not found, but SA_MODULES asks for: $SA_MODULES"
[ -n "${SA_MODULE_TOKEN:-}" ] || die "SA_MODULE_TOKEN is unset. GitHub Packages requires a read:packages token
  even for a public package, and every module repository here is private, so there is no unauthenticated path."

# Read one pinned value. Deliberately parsed rather than sourced: modules.env is plain KEY=VALUE and sourcing it
# would execute whatever it contains.
pin() { busybox grep -E "^$1=" "$MODULES_ENV" | busybox sed -E "s/^$1=//" | tail -1; }

mkdir -p "$TARGET"

for key in $SA_MODULES; do
    package="$(pin "${key}_PACKAGE")"
    version="$(pin "${key}_VERSION")"
    digest="$(pin "${key}_SHA512")"

    [ -n "$package" ] || die "$key has no ${key}_PACKAGE in $MODULES_ENV."
    [ -n "$version" ] || die "$key has no ${key}_VERSION in $MODULES_ENV."
    # A placeholder pin is refused HERE rather than at edit time, because a module nobody installs may honestly
    # have no version yet — it stops being honest the moment a deployment asks for it.
    [ "$version" != "0.0.0" ] || die "$key is pinned at the placeholder 0.0.0 in $MODULES_ENV, but this
  deployment asks to install it. Pin the version the module repository actually published."
    [ -n "$digest" ] || die "$key has no ${key}_SHA512 in $MODULES_ENV. A version without a digest installs
  bytes nobody has checked, which is the thing ADR 0799 exists to end."

    lower="$(echo "$package" | tr 'A-Z' 'a-z')"
    url="$FEED/download/$lower/$version/$lower.$version.nupkg"
    nupkg="/tmp/$lower.$version.nupkg"

    echo "modules-init: fetching $package $version"
    wget -q --header="Authorization: Bearer $SA_MODULE_TOKEN" -O "$nupkg" "$url" \
        || die "could not download $package $version from $FEED.
  wget's own line above says which of the two this is, and they need different fixes: a 404 means the version
  is not published (or the token cannot see it), a refused connection means the feed is unreachable."

    actual="$(busybox sha512sum "$nupkg" | busybox cut -d' ' -f1 | busybox xxd -r -p | busybox base64 | tr -d '\n')"
    [ "$actual" = "$digest" ] || die "$package $version does not match its pinned digest.
  pinned:   $digest
  actual:   $actual
  Nothing was installed. If the version is right, the pin is stale — update ${key}_SHA512 in modules.env."

    # Unpack into a staging directory and swap it in, never over a directory in place. A running host maps a
    # module assembly lazily, so overwriting one under it poisons IL that has not been JITted yet and surfaces
    # as "Bad IL range" hours later. Nothing is running yet at init time, but the safe form costs one mv.
    staging="/tmp/stage-$lower"
    rm -rf "$staging" && mkdir -p "$staging"
    busybox unzip -q "$nupkg" 'lib/*' -d "$staging" || die "$package $version has no lib/ payload."

    dest="$TARGET/$lower"
    rm -rf "$dest.new" && mkdir -p "$dest.new"
    # Only the assemblies, flattened. This is deliberately the same shape the hand-copied DLL had: the host
    # already provides SimplArchive.ModuleAbi, so shipping the package's copy of it alongside would invite the
    # same type loaded twice from two contexts.
    find "$staging/lib" -name '*.dll' -exec cp {} "$dest.new/" \; 2>/dev/null || true
    [ -n "$(ls -A "$dest.new")" ] || die "$package $version unpacked no assemblies."

    rm -rf "$dest" && mv "$dest.new" "$dest"
    rm -rf "$staging" "$nupkg"
    echo "modules-init: installed $package $version into $dest"
done

echo "modules-init: done."
