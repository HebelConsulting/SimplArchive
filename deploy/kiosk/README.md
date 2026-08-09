# Public kiosk demo — Docker Compose

A live, daily-reset showcase of SimplArchive at **https://demo.simplarchive.dev** (object storage at
**https://s3.simplarchive.dev**). See `docs/adr/0489-public-kiosk-docker-compose.md` for the why.

This is a **self-contained bundle** — it runs the published images (`ghcr.io/hebelconsulting/simplarchive` +
`-ocr`) so the host builds nothing, and carries the few config files the sidecars need under `config/`. It is
the same **Development**-environment stack as local dev (OpenIddict dev certs, in-cluster OpenBao, demo seed) —
deliberately, because the kiosk is a throwaway demo wiped daily, **not** a hardened production install.

## Layout

- `docker-compose.yml` — the stack (published app/ocr images + Postgres/SeaweedFS/OpenSearch/Tika/Gotenberg/
  OCR/OpenBao/Valkey/Mailpit) behind a Caddy TLS proxy. Only Caddy publishes host ports (80/443).
- `Caddyfile` — Caddy with **automatic Let's Encrypt** for the two public hostnames.
- `config/` — `db-init.sql`, `seaweedfs-s3.json`, `openbao.hcl` (with `disable_mlock` for OpenVZ),
  `openbao-entrypoint.sh`.
- `reset.sh` — the daily full-wipe-and-reseed (certs preserved).
- `caddy_data/`, `caddy_config/` — created on first run; hold the LE certs (host bind-mounts, so `reset.sh`'s
  `down -v` never wipes them).

## Prerequisites

- A host with Docker + the Compose plugin, ports **80/443 free**, and a public IP.
- DNS: `demo.simplarchive.dev` and `s3.simplarchive.dev` A-records (or CNAMEs) pointing at the host **before**
  first start (Caddy needs them live to issue certs via ACME HTTP-01).

## Deploy

```sh
cd /opt/simplarchive        # where this bundle lives
docker compose up -d        # pulls images; Caddy issues LE certs on first request to :443
docker compose logs -f caddy   # watch the cert being obtained
```

First boot takes a few minutes (image pulls + OpenBao provisioning + the initial search reindex). Then browse
to https://demo.simplarchive.dev and log in as **demo@simplarchive.dev / SimplDemo2026!**.

## Daily reset

Install a cron entry (as root):

```cron
0 4 * * * /opt/simplarchive/reset.sh >> /var/log/kiosk-reset.log 2>&1
```

`reset.sh` wipes every data volume and re-provisions fresh (so there's no OpenBao/Postgres credential drift),
re-pulling `:latest` — so a newly published release goes live within a day. The Caddy certificates survive.

## Notes

- **Not for production.** Development environment, dev certificates, a public demo login with a known password,
  and data reset daily — by design.
- No dev-convenience UIs are exposed (no pgAdmin / Mailpit web / SeaweedFS UIs / direct DB or S3 host ports).
  The Api is bound to `127.0.0.1:8080` for on-host troubleshooting only.
- To refresh the demo immediately (not wait for the nightly reset): `docker compose pull && docker compose up -d`.
