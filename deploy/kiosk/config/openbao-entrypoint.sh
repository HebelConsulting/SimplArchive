#!/bin/sh
# Persistent dev OpenBao entrypoint (ADR "Persist the dev OpenBao"): start the server on the file backend, then
# initialize once + auto-unseal on every start from a key saved in the data volume. This keeps OpenBao's state
# (KV secrets, the DB secrets engine + the rotated `simplarchive` password, AppRole, transit, PKI certs) across
# restarts, so it never drifts from Postgres — the root cause of the recurring `28P01` credential failures.
#
# NOT for production: the single unseal key + root token are written to the data volume so restarts are
# hands-off. Production would use a real seal (auto-unseal / multiple key holders) and never persist the root
# token.
set -e
export BAO_ADDR="http://127.0.0.1:8200"
KEYS="/openbao/init.json"

# Start the server in the background; the script stays as PID 1's child so it can init/unseal, then waits on it.
bao server -config=/etc/openbao/openbao.hcl &
PID=$!

# Wait until the API responds. `bao status` exits 0 (unsealed) or 2 (sealed) once listening; anything else means
# it's not up yet. The status calls are in `if`/`||` so a non-zero exit doesn't trip `set -e`.
while true; do
  if bao status >/dev/null 2>&1; then
    code=0
  else
    code=$?
  fi
  if [ "$code" -eq 0 ] || [ "$code" -eq 2 ]; then
    break
  fi
  sleep 1
done

# Initialize once (persisted to the file backend), saving the single unseal key + root token to the data volume.
if ! bao operator init -status >/dev/null 2>&1; then
  bao operator init -key-shares=1 -key-threshold=1 -format=json > "$KEYS"
  chmod 644 "$KEYS" 2>/dev/null || true
fi

# Auto-unseal from the saved key whenever sealed (every restart lands here). Detect via `bao status` exit code
# (2 = sealed) — the status JSON has spaces (`"sealed": true`), and the key sits on its own line in the
# pretty-printed init.json, so grep/single-line sed of the JSON is unreliable.
if bao status >/dev/null 2>&1; then
  rc=0
else
  rc=$?
fi
if [ "$rc" -eq 2 ]; then
  UNSEAL_KEY=$(sed -n '/"unseal_keys_b64"/{n;p;}' "$KEYS" | tr -d ' ",')
  bao operator unseal "$UNSEAL_KEY" >/dev/null
fi

echo 'openbao ready (unsealed)'
wait "$PID"
