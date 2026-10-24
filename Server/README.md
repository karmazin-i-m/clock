# K-Clock Cloud server

Implements `DESIGN.md`'s v1: device enrollment/transfer, telemetry ingest, account isolation,
and the browser API. Read `DESIGN.md` first — this file only records what has and hasn't been
verified, and what a Docker-capable machine still needs to do.

## What ran and passed in the sandbox that wrote this

That sandbox had **.NET SDK 10.0.112** and network access to nuget.org, but **no Docker** and
no hardware. Accordingly, `DESIGN.md` §14 build-order steps 1 (ESP-01 free-heap measurement)
and 2 (verify the RLS×columnstore conflict on a real `timescaledb-ha` image) were **not
attempted** — the schema instead implements §8's "design that survives either answer" directly
(RLS only on the ordinary tables; `telemetry` and `telemetry_1h` are reached only through
`security_barrier` views, and `clock_app` holds zero grants on either). **Re-confirming §8's
blocker on the real image remains outstanding.** Step 9 (the ESP-01 firmware HTTP client) is
separate, serial work and out of scope here entirely.

Commands that did run, and passed, in that sandbox:

```
dotnet build Server/KClock.slnx
dotnet format Server/KClock.slnx --verify-no-changes
dotnet tool run dotnet-ef migrations add InitialSchema ...
dotnet tool run dotnet-ef migrations add TelemetryHourlyAggregate ...
dotnet tool run dotnet-ef migrations add IsolationViewsAndGrants ...
dotnet tool run dotnet-ef migrations script --idempotent -o /tmp/kclock-migrations.sql
dotnet test tests/KClock.Tests/KClock.Tests.csproj --list-tests
```

The generated migration SQL was reviewed by hand against §15's "don't let through review"
checklist: no `FORCE ROW LEVEL SECURITY`, `binding_id` is in the continuous aggregate's
`GROUP BY`, `set_config(..., true)` is used (never bare `SET`), humidity has a `CHECK` that
makes the `-1` sentinel unrepresentable, `device_access` is keyed on `binding_id`, there is no
`ALTER DEFAULT PRIVILEGES`, and no telemetry column is `NOT NULL DEFAULT`.

Note: `dotnet new sln` on this SDK produces a `KClock.slnx` (the new XML solution format), not
a `.sln` — every command above uses that filename; both work identically with the `dotnet` CLI.

## What is written but could not be run here — run these once Docker is available

```
docker compose -f Server/compose.yaml up -d db
dotnet ef database update --project Server/src/KClock.Data --startup-project Server/src/KClock.Data --context ClockDbContext
dotnet test Server/KClock.slnx
dotnet run --project Server/tools/KClock.FakeDevice -- --url https://api.<domain> --code XXXX-XXXX
Server/scripts/tls-check.sh <domain>
```

Plus, after the first real deploy, the five-second health check from DESIGN.md §13:

```sql
SELECT job_id, proc_name, owner, scheduled, config FROM timescaledb_information.jobs ORDER BY job_id;
```
(expect: compression job `scheduled = true`, retention job `scheduled = false`, aggregate
refresh jobs `scheduled = true`.)

Without Docker, every test fails inside `PostgresFixture.InitializeAsync` with "Docker not
available". That is expected (see the fixture's doc comment).

**With Docker (.NET 10.0.401, Docker 29.8.1), `dotnet test Server/KClock.slnx` passes all 16
tests.** The first run against a real database found six defects the Docker-less sandbox could
not see. All six are fixed, and each has a regression test that fails when the fix is removed:

- EF Core 10.0.12 was resolved against Relational 10.0.4. Every query failed at runtime with
  `FileNotFoundException`, while the build only raised warning MSB3277, which is now an error.
- `INSERT ... ON CONFLICT` on the hypertable needs `SELECT` as well as `INSERT`, so
  `clock_ingest` now has both.
- `SqlQueryRaw<scalar>` needs its column aliased `"Value"`. Without the alias, Google sign-in
  failed.
- `current_account_id()` threw on a pooled connection, because once a scoped transaction has
  ended the GUC reads `''` rather than NULL. It now uses `NULLIF`.
- Humidity outside 0–100, or a missing `samples` field, produced a 500. The device retries a
  500 for ever (§5.3). Humidity outside 0–100 is now stored as NULL, and a batch without
  `samples` gets a 400.
- The enrolment rate limit was never applied (`UseRateLimiter` was missing), and the limiter
  was one window shared by every caller. It is now 10 per minute per client IP.

`ResponseBudgetFilter` also used to write the body itself and return null. The endpoint then
tried to write a second response, so every `/d/v1` request ended in an unhandled exception and
a dropped connection. It now returns an `IResult`.

`KClock.Tests` is still a subset of the cases DESIGN.md §13 lists.

## What you need to supply before any of this works end to end

- A Google Cloud OAuth 2.0 Web client (client id + secret), with redirect URI
  `https://<DOMAIN>/auth/google/callback` registered in the Google Cloud Console first.
- A real domain pointed at the deploy target, for Caddy's automatic TLS.
- A PostgreSQL with TimescaleDB, and the three roles `db/init/00_bootstrap.sql` creates.
- An API host: a plain LXC (below), or Docker Compose for a local stack, whose passwords go in
  a `.env` copied from `.env.example` (gitignored, never commit it).

## Deploying to a plain LXC

Production runs without Docker: PostgreSQL 18 + TimescaleDB in one LXC (192.168.23.8), the API
as a systemd service in another (192.168.23.51), Caddy in a third. `deploy/deploy.sh` builds
self-contained binaries on the workstation and installs them, so the API host carries no SDK,
no .NET runtime and no checkout.

**Database host, once.** TimescaleDB comes from Timescale's own apt repository
(`packagecloud.io/timescale/timescaledb`, package `timescaledb-2-postgresql-18`): Debian's
`timescaledb` package is the Apache build, without the compression the migrations enable —
check `SHOW timescaledb.license` reads `timescale`. Preload it with
`ALTER SYSTEM SET shared_preload_libraries = 'timescaledb'` rather than `timescaledb-tune`,
which also rewrites the memory settings of every other database on the server. Then
`createdb kclock` and run `db/init/00_bootstrap.sql` into it with psql as `postgres`, with the
three `CLOCK_*_PASSWORD` variables exported — it reads them with `\getenv`. `pg_hba.conf` lets
the three roles into `kclock` from the API host only:

```
host  kclock  clock_migrator,clock_app,clock_ingest  192.168.23.51/32  scram-sha-256
```

PostgreSQL takes the first matching line, so this must sit above any broader `host all all`
rule — a Community Scripts install ships with `0.0.0.0/0`, which should go altogether.

**API host, once** (Debian 13, as root):

```bash
apt install -y libicu76 ca-certificates tzdata rsync curl
useradd --system --home /var/lib/kclock --shell /usr/sbin/nologin kclock
install -d -o kclock -g kclock -m 700 /var/lib/kclock/dp-keys
install -d -m 755 /opt/kclock /etc/kclock
# /etc/kclock/kclock.env from deploy/kclock.env.example, mode 600
# /etc/kclock/migrator.conn, mode 600, one line:
#   Host=192.168.23.8;Database=kclock;Username=clock_migrator;Password=...
```

**Every deploy**, from the workstation with an SSH key on the API host:

```bash
Server/deploy/deploy.sh            # or: deploy.sh <host>, or KCLOCK_HOST=<host>
```

It runs the migrations before it replaces the API, so a failed migration leaves the old
version running; it installs `deploy/kclock-api.service` each time, so the unit is edited in
the repo and never on the host; and it fails unless `/health/ready` answers within 20 s.

In Proxmox, start the database container first (`--startup order=1,up=30`, the API
`order=2`), and firewall port 8080 to the Caddy host: the API trusts `X-Forwarded-*` only
from `ReverseProxy__TrustedProxies`, but nothing else should be reaching it either.

## The cabinet (Server/web)

The account pages of `design/` as a React + Vite + TypeScript SPA. It needs Node 20 or later.

```bash
cd Server/web
npm ci
npm test            # vitest: LED glyphs, chart gaps, night dimming, Ukrainian plurals
npm run typecheck
npm run dev         # http://localhost:5173, proxying /api and /auth to the API on :8080
```

`npm run dev` expects the API on `localhost:8080` (`KCLOCK_API` overrides it). Sign-in goes
through Google, so the OAuth client must also list `http://localhost:5173/auth/google/callback`
for local use — or sign in on the API's own port and reuse the cookie.

`dotnet publish` of `KClock.Api` runs `npm ci && npm run build` and ships `web/dist` as the
API's `wwwroot`, so `deploy/deploy.sh` now needs Node on the workstation. The Docker image
builds it in a Node stage instead and publishes with `-p:SkipCabinetBuild=true`. A publish
without a built cabinet fails rather than shipping an API with no pages.

Not verified in a browser by whoever wrote it: the sandbox had no browser that would start.
Everything below the pages is covered — `dotnet test` drives the API they call, and vitest the
drawing code — but the pages themselves want one look in both themes and at phone width.

## A few judgment calls this implementation made that DESIGN.md doesn't spell out

These are documented in more detail in the doc comments of the files named:

- **Two `DbContext` types** (`ClockDbContext` for the browser path/`clock_app`,
  `IngestDbContext` for the device path/`clock_ingest`) rather than one — the three-role model
  in §8 needs two separate runtime connections, not just separate SQL grants.
- **`find_or_create_account`**, a `SECURITY DEFINER` SQL function (in the
  `IsolationViewsAndGrants` migration), is what lets the Google sign-in callback create or
  resolve an `account` row before any `app.account_id` GUC exists to satisfy `account`'s own
  RLS policy. Not named in DESIGN.md's schema section, but a direct extension of the same
  `SECURITY DEFINER` pattern §8 already uses for telemetry.
- **`device_id` on the wire.** DESIGN.md §5.1's enroll response example shows
  `"device_id":"7f3ab2c1-…"` (quoted, UUID-shaped), but §7's schema declares `device.id` as a
  plain `bigint` identity column, and the rest of the frozen contract renders integers
  unquoted. Implemented here as an unquoted JSON number, consistent with `device.id`'s actual
  type and with `seq`/`ts`/`cfgv` elsewhere in the same contract — treated as a documentation
  inconsistency, not a deliberate string-typed id. Worth a decision either way, since it's the
  one deviation from a literal example in `DESIGN.md`.
- **`EFCore.NamingConventions`** and **`prometheus-net.AspNetCore`** are the two NuGet
  dependencies not named anywhere in DESIGN.md. The first maps every entity's PascalCase
  properties to the schema's snake_case columns without ~80 hand-written `HasColumnName`
  calls; the second is what `/metrics` (explicitly required by §12 Ops) is built on.
