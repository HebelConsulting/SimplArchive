# SimplArchive

**A showcase of how a senior, AI-driven software developer can produce a complex, enterprise-grade system in a relatively short period of time.**

SimplArchive is a working, multi-tenant **Document Management System**, built end-to-end as a demonstration of AI-assisted software engineering: Clean Architecture, a hypermedia REST API, two clients (a Blazor WebAssembly web app and a cross-platform Avalonia desktop app), OAuth/OIDC auth with MFA and passkeys, full-text + OCR search, document previews, versioning, ACLs, workflow, audit trails, legal hold & retention, WebDAV, a **fully localized UI (English, German, Italian, Spanish)**, and a production Helm chart — with an extensive automated test suite (unit, integration, container-backed E2E, and browser/desktop UI E2E) and a hardened CI pipeline.

## ▶ Try the live demo

A live instance is running at **<https://demo.simplarchive.dev>** — log in and explore, nothing to install:

| | |
|---|---|
| **URL** | **<https://demo.simplarchive.dev>** |
| **Email** | `demo@simplarchive.dev` |
| **Password** | `demo1234` |

It resets to a clean, known state every night, so feel free to create, upload, workflow, and delete anything. The user manual is one click away at [demo.simplarchive.dev/download/manual/](https://demo.simplarchive.dev/download/manual/).

> **Not for production as shipped.** The default stack uses development certificates and fixed demo credentials. It is an enterprise-grade *architecture and feature* showcase; a real production posture still requires hardening the deployment (real secrets/certificates, managed dependencies, and load/scale validation).

## Run it locally (Docker Compose)

The whole stack — API + web/desktop-serving host, Postgres, S3-compatible object storage, OpenSearch + Tika, Gotenberg, OCR, mail catcher, secret store, and a pgAdmin UI — runs from one file:

```bash
docker compose up --build
```

Then open **http://localhost:8080** and log straight into the UI with the seeded demo account:

- **Email:** `demo@simplarchive.local`
- **Password:** `demo1234`

It comes pre-seeded with a demo tenant, a sample repository/document, and a workflow in progress, so there's something to explore immediately. (The same file runs under Podman: `podman compose up --build`.) Handy dev UIs: pgAdmin at http://localhost:5050 (auto-connected to the database) and the Mailpit inbox at http://localhost:8025.

The entire UI — both clients plus the shared sign-in page — is available in **English, German, Italian, and Spanish**. Switch language from the flag menu in the web app bar (next to the notifications bell); the desktop app picks it on the logon window.

To try it from **another device on your network** (a phone, a second laptop), set `PROXY_HOST` to this machine's LAN IP/hostname and `S3_PUBLIC_URL` to the matching object-storage proxy URL — an optional Caddy reverse proxy then serves both the app (port 9443) and object storage (port 9444) over HTTPS:

```bash
PROXY_HOST=192.168.1.50 S3_PUBLIC_URL=https://192.168.1.50:9444 docker compose up --build
```

On the device, first visit **https://192.168.1.50:9444** once and accept the self-signed-certificate warning (needed so document previews/uploads work), then browse **https://192.168.1.50:9443** and accept its warning too. (Dev/test only.)

## Native desktop app

Alongside the web client, SimplArchive ships a cross-platform **Avalonia desktop client**. Build self-contained installers/archives — no .NET needed on the target machine:

```bash
scripts/package-macos-dmg.sh          # macOS → dist/SimplArchive-<version>-{arm64,x64}.dmg   (run on macOS)
scripts/package-windows-linux.sh      # Win/Linux x64 → dist/SimplArchive-<version>-win-x64.zip + -linux-x64.tar.gz
```

All are self-contained and **unsigned** (a showcase build): macOS Gatekeeper warns on first open (right-click → **Open**); Windows SmartScreen warns on first run ("More info" → "Run anyway"); the Linux launcher may need `chmod +x`. The Windows/Linux script cross-builds from any OS with the .NET SDK.

## Install in production

Production deploys via the **Helm chart** in [`charts/simplarchive`](charts/simplarchive) — an API Deployment/Service/Ingress/HPA/PDB with health probes, non-root containers, and secret wiring; dependencies (Postgres, object storage, OpenSearch, …) are external/managed. The chart's [`values.yaml`](charts/simplarchive/values.yaml) documents the full configuration surface, and pre-install/pre-upgrade migration hooks apply schema changes off the app's startup path.

## Architecture at a glance

- **Clean Architecture** — `Domain` → `Application` → `Infrastructure`/`Auth` → `Api` → `Client`/`Worker`, with the dependency direction enforced as tests.
- **Multi-tenant by construction** — global tenant query filters, per-tenant object-storage buckets, and per-tenant tamper-evident (hash-chained, WORM-sealed) audit trails.
- **Hypermedia REST API** — RFC 7807 problem details, ETag/`If-Match` optimistic concurrency, media-type API versioning, and independent JSON/XML content negotiation.
- **Documents** — a unified tree where a repository, a folder, and a leaf are all one `Document` type; immutable versioned metadata "masks"; EAV index fields; document-scoped ACLs with inheritance + override.
- **Search & preview** — OpenSearch full-text over content (Apache Tika, incl. OCR), faceted navigation, search hit-overlays, and on-demand previews/renditions (images, Office → PDF via Gotenberg, email, Markdown, …).
- **Enterprise features** — approval workflow, notifications (in-app + email + real-time), legal hold & retention with WORM immutability, check-out/check-in, a WebDAV gateway, MFA (TOTP + passkeys), and OpenBao-backed secrets.
- **Two clients** — a Blazor WebAssembly web workbench and a native Avalonia desktop client, both driving the same API.

## Tech stack

.NET 10 · ASP.NET Core · Blazor WebAssembly · Avalonia · EF Core (PostgreSQL) · OpenIddict · OpenSearch + Apache Tika · Gotenberg · S3-compatible object storage · OpenBao · Serilog · Docker/Kubernetes.

## License

Licensed under the [Apache License 2.0](LICENSE).
