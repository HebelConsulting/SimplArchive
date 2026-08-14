# Licence gate inputs

The fast CI tier runs `nuget-license` over the whole solution's transitive tree and **fails the build** on any
licence outside the allowlist (ADR 0381). These three files are its inputs.

They are strict JSON: `nuget-license` parses them with `System.Text.Json` at its default settings, so **a `//`
comment breaks the build** (`'/' is invalid after a value`). That is why this file exists — the rationale
cannot live in the data.

| File | What it is |
| --- | --- |
| `allowed-licenses.json` | the licences compatible with our Apache-2.0 licensing |
| `package-license-overrides.json` | packages whose metadata is wrong or missing, pinned by version so a bump re-opens the question |
| `ignored-packages.json` | packages excluded from the gate entirely — the exception, see below |

## `ignored-packages.json`: one library, two very different reasons

Every entry is the LGPL native **libvips**, dynamically linked. They are not equivalent:

**`NetVips.Native.linux-musl-{x64,arm64}` — permanent.** The product's Docker image is Alpine, and these are the
natives it ships. They are a considered part of the product.

**`NetVips.Native.linux-{x64,arm64}` and `NetVips.Native.osx-{arm64,x64}` — debt, with a trigger to retire
them ([#496](https://github.com/HebelConsulting/SimplArchivePrivate/issues/496)).** They exist only because
Ubuntu 24.04 ships libvips **8.15.1** while NetVips 3.2.0 needs **8.18.4**; installing the distro package made
the **test host crash** rather than fail cleanly. They are referenced by **test projects only**, so nothing
extra is published — verify with `dotnet list src/SimplArchive.Api/SimplArchive.Api.csproj package
--include-transitive`.

**When the runner image ships a compatible libvips, delete those four entries and their four
`PackageReference`s together.** #496 lists every file and — more importantly — how to verify the environment is
genuinely fixed, which is not "CI went green": the Debian `sdk:10.0` image passes where the Ubuntu
`sdk:10.0-noble` one crashes, so reproducing on the wrong base image will appear to clear it.

## What stops this drifting

`nuget-license` matches by package **id** with no notion of which project referenced it, so an exception granted
for tests silently licenses the same package in a shipping project. `TestOnlyNativePackageTests` closes that in
both directions: no `src/` project may reference a test-only native, and no test-only native may sit in this
file without a test project actually using it — so an exception cannot outlive its reason unnoticed.

See also the standing principle in `CLAUDE.md` ("a workaround dependency is DEBT, and carries the trigger that
retires it") and ADR 0576.
