-- Establishes the application-owning database roles (ADR 0358, "Dedicated migration owner role").
-- Idempotent + safe to re-run. Executed by the compose `db-init` one-shot as the postgres *bootstrap* superuser
-- — the only thing postgres is ever used for. After this runs, `postgres` owns nothing application-related and
-- is never used by the app, migrations, or OpenBao again.
--
--   * simplarchive       (LOGIN)             — OWNS the database + schema + every object; runs DDL migrations.
--                         Its password is OpenBao-managed + rotated (a database static role), so the literal
--                         below is only a one-time bootstrap seed OpenBao overwrites on first rotation.
--   * simplarchive_vault (LOGIN, CREATEROLE, NOINHERIT) — the identity OpenBao's database engine connects as to
--                         mint the dynamic runtime roles + rotate the static role; root-rotated by OpenBao (its
--                         literal is a bootstrap seed too). NOINHERIT + admin-only membership so it can
--                         administer `simplarchive`/`simplarchive_app` without inheriting the owner's rights.
--   * simplarchive_app   (NOLOGIN group)     — the least-privilege runtime bundle; the OpenBao dynamic
--                         per-startup roles join it (IN ROLE) and inherit its DML grants.
--
-- ALTER DEFAULT PRIVILEGES makes anything `simplarchive` creates auto-granted (DML) to the app group, so a
-- table created during a migration is usable by an already-minted dynamic role regardless of ordering.

DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'simplarchive_app') THEN
    CREATE ROLE simplarchive_app NOLOGIN;
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'simplarchive') THEN
    CREATE ROLE simplarchive LOGIN PASSWORD 'simplarchive_bootstrap';
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'simplarchive_vault') THEN
    CREATE ROLE simplarchive_vault LOGIN CREATEROLE NOINHERIT PASSWORD 'simplarchive_vault_bootstrap';
  END IF;
END
$$;

-- simplarchive administers the app group so it can add each newly-minted dynamic role to it.
GRANT simplarchive_app TO simplarchive WITH ADMIN OPTION;

-- The OpenBao engine admin administers both roles: WITH ADMIN OPTION lets it add dynamic roles to the app group
-- and rotate `simplarchive`'s password (ALTER ROLE ... PASSWORD needs CREATEROLE + ADMIN on the target). Paired
-- with the role's NOINHERIT attribute, it can administer without inheriting the owner's privileges.
GRANT simplarchive_app TO simplarchive_vault WITH ADMIN OPTION;
GRANT simplarchive TO simplarchive_vault WITH ADMIN OPTION;

-- Reset the two OpenBao-managed passwords to their bootstrap seeds on every run. openbao-init re-runs whenever
-- the api is recreated and rewrites its database engine config back to these bootstrap seeds, so Postgres must
-- match for its rotate-root (as simplarchive_vault) to reconnect. openbao-init then immediately re-rotates BOTH
-- (rotate-root for the vault admin, and — crucially — an explicit rotate-role for the simplarchive static role,
-- ADR "Fix OpenBao static-role drift on re-provision"), so these literals never persist as the live passwords
-- and OpenBao's store + Postgres always re-agree. Without that static-role rotation the reset here would strand
-- Postgres on the bootstrap seed while OpenBao kept its old rotated cred → `28P01`. Harmless on a fresh database.
ALTER ROLE simplarchive PASSWORD 'simplarchive_bootstrap';
ALTER ROLE simplarchive_vault PASSWORD 'simplarchive_vault_bootstrap';

-- simplarchive owns the database + schema (so it can freely CREATE/ALTER everything).
ALTER DATABASE simplarchive OWNER TO simplarchive;
ALTER SCHEMA public OWNER TO simplarchive;
GRANT USAGE ON SCHEMA public TO simplarchive_app;

-- Transition: move any objects created by a prior owner (postgres, or the earlier `simplarchive_owner`) to
-- simplarchive, then retire that earlier role. Targeted to `public` (not a superuser-wide REASSIGN OWNED).
DO $$
DECLARE
  r record;
BEGIN
  FOR r IN SELECT tablename FROM pg_tables WHERE schemaname = 'public' LOOP
    EXECUTE format('ALTER TABLE public.%I OWNER TO simplarchive', r.tablename);
  END LOOP;
  FOR r IN SELECT sequencename FROM pg_sequences WHERE schemaname = 'public' LOOP
    EXECUTE format('ALTER SEQUENCE public.%I OWNER TO simplarchive', r.sequencename);
  END LOOP;
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'simplarchive_owner') THEN
    EXECUTE 'REASSIGN OWNED BY simplarchive_owner TO simplarchive';
    EXECUTE 'DROP OWNED BY simplarchive_owner';
    EXECUTE 'DROP ROLE simplarchive_owner';
  END IF;
END
$$;

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO simplarchive_app;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO simplarchive_app;

ALTER DEFAULT PRIVILEGES FOR ROLE simplarchive IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO simplarchive_app;
ALTER DEFAULT PRIVILEGES FOR ROLE simplarchive IN SCHEMA public
  GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO simplarchive_app;

-- The MTA's read-only role (ADR 0628). Postfix asks one question of this database — "is this domain one we
-- accept?" — so it gets exactly the privilege to ask it and nothing else. A single shared credential would
-- have let the component most exposed to the internet read every document row in the archive.
--
-- The grant is deliberately per-table rather than schema-wide: TenantMailDomains is created by a migration, so
-- the grant is applied idempotently below once the table exists, and stays absent (harmlessly) until then.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'simplarchive_postfix') THEN
    EXECUTE 'CREATE ROLE simplarchive_postfix WITH LOGIN PASSWORD ''postfix''';
  END IF;
END
$$;

GRANT CONNECT ON DATABASE simplarchive TO simplarchive_postfix;
GRANT USAGE ON SCHEMA public TO simplarchive_postfix;

DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM information_schema.tables
             WHERE table_schema = 'public' AND table_name = 'TenantMailDomains') THEN
    EXECUTE 'GRANT SELECT ON public."TenantMailDomains" TO simplarchive_postfix';
  END IF;
END
$$;

-- NOTE: deliberately NO `ALTER DEFAULT PRIVILEGES … GRANT SELECT ON TABLES` for this role. That would have
-- been the convenient way to cover the run where the table does not exist yet, and it would have granted read
-- on every table the migrations create from then on — handing the most internet-exposed component in the
-- stack the whole archive, under a comment claiming least privilege. The grant above is conditional instead,
-- and `postfix-grant` in the compose stack re-applies it once the migration has created the table.
