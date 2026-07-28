# Persistent dev-stack OpenBao (ADR "Persist the dev OpenBao"): a file storage backend + a named volume, so
# OpenBao keeps its secrets across container restarts (machine sleep, Docker restart) instead of the in-memory
# `-dev` mode losing everything — which caused the OpenBao<->Postgres credential drift. NOT for production
# (single unseal key stored in the data volume for a hands-off dev restart; no TLS).
# disable_mlock: the kiosk runs on an OpenVZ container VPS where mlock isn't permitted; without this OpenBao
# fails to start. Acceptable for a dev-grade throwaway demo (ADR 0489).
disable_mlock = true

storage "file" {
  path = "/openbao/file"
}

listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = true
}

api_addr = "http://openbao:8200"
ui       = true
