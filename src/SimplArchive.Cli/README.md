# saconsole — the SimplArchive administrative CLI

## What SimplArchive is

**SimplArchive** is an open-source (Apache-2.0) **enterprise document management system**: an archive in which an
organisation files, finds, controls and keeps its documents. It covers the essentials of the category:

- **Repositories and folders**, with **index data** described by masks (typed fields per kind of document) and full
  **version history**.
- **Full-text search** across content, names and index data.
- **Permissions** per document, inherited down the tree, with groups, service accounts and clearance levels.
- An **approval workflow** with a task inbox, escalation and reminders.
- An append-only, **hash-chained audit trail**, with retention rules, **legal holds** and WORM storage.
- **Multi-tenancy**, **MFA and passkeys**, and optional **encryption at rest** with smartcard-based reading.
- **Standard protocols**, so an archive can be mounted as a drive (WebDAV) or read from a mail, calendar or address
  book client (IMAP, CalDAV, CardDAV).
- A **web client** and a cross-platform **desktop client**, and **industry modules** that extend the core per tenant.

It runs as a container image beside PostgreSQL, S3-compatible object storage and a search engine, with a Helm chart
for production. The project is also a showcase of how far an AI-assisted, senior-led development process can take an
enterprise-grade system: <https://github.com/HebelConsulting/SimplArchive>

## What saconsole does

`saconsole` administers a SimplArchive installation over its HTTP API, **following the links the API advertises**
rather than composing addresses, so it keeps working when routes move. It is how an installation is set up and
configured from a terminal or a script:

| Command | For |
|---|---|
| `login`, `whoami` | Sign in (interactively, or as a service account) and show who you are acting as |
| `tenant create`, `tenant standard-repository` | Provision a tenant with its first administrator; choose where SimplArchive files what it brings |
| `module list`, `module activate`, `module settings`, `module rebuild` | Activate industry modules with a vendor-signed licence, configure them, rebuild their read models |
| `certificates list`, `enrol`, `revoke`, `import` | Manage the reader certificates encrypted content is addressed to, including bulk import from a CA |
| `acl set`, `grant`, `revoke` | Decide who may do what to a document |
| `me certificate`, `intray previous` | A user's own certificate, and recovering intray content |

Every command has `--help`.

## Install and sign in

```sh
dotnet tool install --global HebelConsulting.SimplArchive.Cli

# interactive: approve the sign-in in your browser, then the session is set for this shell
eval "$(saconsole login --url https://archive.example.com)"
saconsole whoami
```

For unattended use, sign in as a tenant **service account** with `saconsole login --client-id <id>`, with the secret
in `SACONSOLE_CLIENT_SECRET` (from the environment, never as an argument).

Requires the .NET 10 runtime. Source, documentation and issues: <https://github.com/HebelConsulting/SimplArchive>
