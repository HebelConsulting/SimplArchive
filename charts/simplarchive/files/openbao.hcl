# In-cluster OpenBao (kiosk, ADR 0485): file storage backend + a PVC so state (KV, DB engine + the rotated
# simplarchive password, AppRole, transit, PKI certs) survives restarts. NOT production-hardened (single unseal
# key persisted to the data volume for hands-off restart; no TLS).
storage "file" {
  path = "/openbao/file"
}

listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = true
}

api_addr = "http://127.0.0.1:8200"
ui       = true
