namespace SimplArchive.Api.Configuration;

// Coordinates for reading secrets from OpenBao (ADR "Secrets management with OpenBao — config credentials").
// These are the non-secret bootstrap values (address + AppRole ids + non-secret DB connection parts) read from
// appsettings/env; the actual secrets (DB password via dynamic creds, MinIO/SMTP/bootstrap via KV) are fetched
// from OpenBao. When Address is empty the whole provider is disabled and appsettings/env are used as-is.
public sealed class OpenBaoOptions
{
    public const string SectionName = "OpenBao";

    // The OpenBao base address, e.g. http://openbao:8200. Empty => the provider is a no-op (disabled).
    public string Address { get; set; } = string.Empty;

    // AppRole machine auth: the RoleId (non-secret) + SecretId (the one bootstrap secret the app is given, via
    // env/file). In dev these are fixed values provisioned by openbao-init; in production the SecretId would be
    // response-wrapped and short-lived.
    public string RoleId { get; set; } = string.Empty;
    public string SecretId { get; set; } = string.Empty;

    // The KV v2 mount (default "secret") the static secrets live under, and the database secrets-engine role
    // (default "simplarchive") that mints dynamic Postgres credentials.
    public string KvMount { get; set; } = "secret";
    public string DatabaseRole { get; set; } = "simplarchive";

    // The non-secret part of the Postgres connection (host/port/database); the dynamic Username/Password from
    // OpenBao's database engine are appended to form ConnectionStrings:Default. When empty, the dynamic DB
    // credential is skipped (only the KV secrets are sourced).
    public string DatabaseConnectionTemplate { get; set; } = string.Empty;

    // The database secrets-engine *static* role whose password OpenBao owns + rotates for the schema-owning
    // migration identity (ADR "OpenBao static-role rotation for the migration owner"). Read from
    // database/static-creds/<name> and composed into ConnectionStrings:Migration. Empty => skipped (migrations
    // fall back to ConnectionStrings:Default), so tests / non-OpenBao deployments are unaffected.
    public string DatabaseOwnerStaticRole { get; set; } = string.Empty;

    // The database secrets-engine *static* role for the RUNTIME connection — a fixed login whose password
    // OpenBao rotates. When set, ConnectionStrings:Default is composed with that username and NO password, and
    // the password is supplied at connect time by OpenBaoDatabasePasswordProvider so it can be re-read as it
    // rotates.
    //
    // This is what makes the app outlive one credential lifetime. The DYNAMIC role below mints a new USERNAME
    // per lease, so its credential cannot be refreshed in place: the app read it once at startup, and at
    // default_ttl (24h) Postgres revoked the role and every new connection failed 28P01 until a restart.
    // Empty => the dynamic credential is used exactly as before, which is what keeps tests and non-OpenBao
    // deployments unaffected.
    public string DatabaseRuntimeStaticRole { get; set; } = string.Empty;
}
