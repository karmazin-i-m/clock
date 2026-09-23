-- Runs once, as the superuser, via docker-entrypoint-initdb.d (DESIGN.md §12).
-- Everything after this file is owned by EF migrations (running as clock_migrator) or by
-- hand-written migration SQL — nothing else in this repo runs as superuser again.

CREATE EXTENSION IF NOT EXISTS timescaledb;
CREATE EXTENSION IF NOT EXISTS citext;

-- Passwords come from the environment, never hardcoded here. psql's \getenv (not a plain SQL
-- feature — this file must be run by psql, which is exactly what
-- docker-entrypoint-initdb.d does for a .sql file) reads them from the container's own
-- environment, sourced from compose's .env (see Server/.env.example).
\getenv migrator_password CLOCK_MIGRATOR_PASSWORD
\getenv app_password CLOCK_APP_PASSWORD
\getenv ingest_password CLOCK_INGEST_PASSWORD

-- clock_migrator: owns the schema, runs DDL. Exempt from RLS as owner — this is why
-- FORCE ROW LEVEL SECURITY must never be set anywhere in this repo (DESIGN.md §8).
CREATE ROLE clock_migrator LOGIN PASSWORD :'migrator_password'
    NOSUPERUSER NOCREATEDB NOCREATEROLE;

-- clock_app: the browser API. RLS policies apply; zero privileges on telemetry/telemetry_1h
-- themselves, reached only through the security_barrier views the IsolationViewsAndGrants
-- migration creates.
CREATE ROLE clock_app LOGIN PASSWORD :'app_password'
    NOSUPERUSER NOCREATEDB NOCREATEROLE;

-- clock_ingest: writes telemetry, reads/writes device credentials. Permissive policies on the
-- ordinary tables (it legitimately crosses accounts to resolve a device's binding); never
-- BYPASSRLS — that needs superuser to grant, which is not available on every managed Postgres
-- this might move to, and a policy is visible in pg_policies where a policy belongs
-- (DESIGN.md §8).
CREATE ROLE clock_ingest LOGIN PASSWORD :'ingest_password'
    NOSUPERUSER NOCREATEDB NOCREATEROLE;

-- clock_migrator owns the schema so it can run DDL without superuser. The other two roles get
-- USAGE only here — their actual table/view/function grants are issued table-by-table in the
-- IsolationViewsAndGrants migration, never via ALTER DEFAULT PRIVILEGES (DESIGN.md §15 item 7).
ALTER SCHEMA public OWNER TO clock_migrator;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO clock_app, clock_ingest;
