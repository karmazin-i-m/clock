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

`dotnet test` here fails all four isolation tests and all four contract tests with a clean
"Docker not available" error raised inside each test's own `PostgresFixture.InitializeAsync`
— that is the expected, documented failure mode in this sandbox (see that fixture's doc
comment), not a bug. `KClock.Tests` is a representative subset of DESIGN.md §13's full case
list, not the exhaustive one — extending `Contract/DeviceContractTests.cs` and
`Isolation/TransferIsolationTests.cs` with the remaining enumerated cases (out-of-order batch,
future timestamp, all-samples-out-of-window) is straightforward follow-up work once there is a
machine to actually run them on.

## What you need to supply before any of this works end to end

- A Google Cloud OAuth 2.0 Web client (client id + secret), with redirect URI
  `https://<DOMAIN>/auth/google/callback` registered in the Google Cloud Console first.
- A real domain pointed at the deploy target, for Caddy's automatic TLS.
- Docker + Docker Compose on the deploy target.
- Chosen passwords for `clock_migrator` / `clock_app` / `clock_ingest` and the Postgres
  superuser, in a `.env` copied from `.env.example` (gitignored, never commit it).

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
