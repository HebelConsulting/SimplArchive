# Persistent dev-stack OpenBao (ADR "Persist the dev OpenBao"): a file storage backend + a named volume, so
# OpenBao keeps its secrets across container restarts (machine sleep, Docker restart) instead of the in-memory
# `-dev` mode losing everything — which caused the OpenBao<->Postgres credential drift. NOT for production
# (single unseal key stored in the data volume for a hands-off dev restart; no TLS).
# NOT under /openbao (#1642): the image declares VOLUME /openbao/file, so an ANONYMOUS volume shadows that
# directory even when a named volume is mounted at /openbao. The anonymous one does not survive `docker compose
# down`, and the next start silently re-initialised an empty OpenBao (new unseal and transit keys, every
# transit-encrypted secret unreadable). /openbao-state is ours alone. Kubernetes ignores image VOLUMEs, so the chart
# is not affected.
storage "file" {
  path = "/openbao-state/file"
}

listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = true
}

api_addr = "http://openbao:8200"
ui       = true
