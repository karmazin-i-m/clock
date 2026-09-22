# K-Clock Cloud — server design

Design document for the C# backend. Nothing here is implemented yet; this is the
specification the implementation is meant to follow, written while the decisions were
still fresh. It is deliberately opinionated, and it records *why* as much as *what* —
several of the choices below look arbitrary until you know what they are avoiding.

Language note: this file is in English to match `CLAUDE.md` and `README.md`. The user
facing strings on the ESP's local pages stay Ukrainian, as they are today.

---

## 1. What this is for

The clock in this repository is a standalone device: a Nano driving a 24x8 matrix, a
DS3231 for time, a BME280 or BMP280 for the environment, and an ESP-01 on the UART that
today does nothing but a captive portal, NTP and two local web pages.

The product intent is to sell many of these to many individual people. That needs a
server which:

- gives a person an account (Google sign-in) holding **many clocks**;
- lets them **bind** a particular clock to that account;
- **ingests and stores** the sensor telemetry each clock reports;
- later, serves **statistics to a SPA**.

Two requirements shape everything else:

1. **Traffic is encrypted with standard TLS**, not a bespoke scheme.
2. **A clock can be handed to a new owner, and the new owner must never see the previous
   owner's data.** The previous owner keeps their own history.

The clock must keep working with no server at all. That principle is already in the
firmware — *pull the ESP out and the clock behaves exactly as before* — and the cloud
does not get to weaken it.

## 2. Scope

**v1 is ingest, accounts and binding. Nothing else.**

Explicitly deferred to v2, with URL shapes and envelope fields reserved now so that v2 is
purely additive: configuration downlink, server-supplied time replacing NTP, the
chart/aggregate API, and OTA firmware distribution.

The telemetry **response envelope ships in v1** even though it carries almost nothing,
because a fleet in the field cannot be reflashed to accept a new response shape.

## 3. Decisions

| Area | Decision |
|---|---|
| Transport | HTTPS; one API, two device profiles (`esp8266-1m` now, `esp32` later) |
| Hosting | self-hosted VPS, docker compose, Caddy terminating TLS |
| Store | one PostgreSQL with TimescaleDB |
| Cadence | sample every 10 s, flush a batch every ~60 s; both server-controlled |
| Retention | keep forever; the policy exists but is disabled |
| Provisioning | single-use enrollment code, typed into the clock's local settings page |
| Ownership | a **period** (`device_binding`), not a field |
| Isolation | PostgreSQL, enforced below the application — see §8 |
| Transfer | previous owner keeps their history; new owner cannot see it |
| Sign-in | Google only, modelled as `account` + `external_login(provider, subject)` |
| Sharing | one owner in v1, but access resolves through `device_access` with role `owner` |
| UI | SPA, same origin, cookie session — next step |
| Settings | the cloud is authoritative; the local page is the emergency path |
| Offline | ~30–60 min ring buffer in ESP RAM |
| Location | this repository, `Server/` |
| Ops | structured logs, `/health`, `/metrics`, `pg_dump` in cron |

Verified locally: **.NET SDK 10.0.401**, **Docker 29.8.1**.

---

## 4. Why the transport is HTTPS, and what it costs

The requirement is standard TLS. On an ESP-01 that is genuinely near the limit, and the
design has to respect the limits rather than wish them away.

- The image is **~400 KB against a ~502 KB OTA ceiling** (both images live in flash during
  an update). `ESP8266HTTPClient` + BearSSL is **+50–80 KB**, landing around 470–480 KB.
  Crossing the ceiling breaks OTA *silently*, with no diagnostic.
- **Free heap has never been measured.** `ESP.getFreeHeap()` is not called anywhere in
  `Clock_ESP01/Clock_ESP01.ino`. A full BearSSL handshake needs **16–22 KB of contiguous
  block** while the web server, WiFiManager, ArduinoOTA and three name responders are all
  resident.
- **MFLN is almost certainly unavailable.** Go's `crypto/tls`, which Caddy uses, does not
  implement RFC 6066 `max_fragment_length`; neither does nginx. So
  `probeMaxFragmentLength()` will fail and the device falls back to 16 KB buffers. **Do
  not design assuming 6–8 KB handshakes.**
- **Session resumption is the real mitigation** and it is fragile. It removes the
  certificate from every handshake after the first. It needs a `BearSSL::Session` to
  survive in RAM across the 60 s gap, and the server to actually issue resumable tickets.
  Verify with `openssl s_client -reconnect` before relying on it.
- Certificate: **ECDSA** from Let's Encrypt (Caddy `key_type p256`) for a short chain.
  Validate against a trust anchor in PROGMEM. **Never** fingerprint pinning — Let's
  Encrypt reissues every 90 days and would blind the whole fleet. Never `setInsecure()`.

### Step zero, before any server code

Flash a throwaway sketch that brings up the existing web server, prints
`ESP.getFreeHeap()` and `ESP.getMaxFreeBlockSize()`, and attempts one real TLS handshake
with a single trust anchor. **If the largest free block is under ~18 KB, the `esp8266-1m`
profile is dead** and the answer is an ESP-12F or ESP32. The server design survives either
outcome — that is what the two profiles are for — but the schedule does not. This is an
hour of work and it gates everything.

### Why 10 seconds does not mean 10 requests per minute

8 640 connections per device per day at a 1.5–4 s handshake is 4–9 hours a day of a device
that is supposed to be polling the Nano every 3 s with a 15 s staleness threshold
(`LinkStaleMs`). It is not slow, it is inoperable.

So sampling and sending are separated: **sample into a RAM ring every 10 s, flush a batch
over one connection every 60 s.** Resolution is unchanged, connections drop by 6x, and
with session resumption the device spends minutes a day on the network instead of hours.

---

## 5. The device contract

Base `https://api.<domain>`, device paths under `/d/v1/`. **This path is frozen.** You
cannot conveniently reflash a fleet, so v1 is additive-only forever. A `/d/v2/` would only
appear if the encoding itself changed.

### Rules that bind the server

1. Every device response is a **JSON object** at the top level. Never an array, never a
   bare scalar.
2. Response bodies are **≤ 512 bytes** in v1 and ≤ 1024 ever. Enforced by an endpoint
   filter *and* by a test.
3. **No `Content-Encoding` on `/d/*`.** Caddy's `encode` must exclude the path. A 40 KB
   heap device must not be inflating anything. That exclusion is load-bearing and belongs
   commented in the Caddyfile, because it looks like dead configuration.
4. **No chunked transfer encoding on `/d/*`.** Always a known `Content-Length`.
5. Every response key is **unique as a substring** across the contract and never appears
   inside a string value. This is not aesthetics: it means firmware that cannot afford
   ArduinoJson can parse with `strstr("\"sample_s\":")` + `atoi` and still be correct. If
   flash runs out, you drop the JSON library, not the cloud feature.
6. The device **ignores unknown keys**.
7. Reserved for v2, never emitted in v1: `config`, `time`, `fw`, `token`, `msg`.

Minimal API, not controllers, and the reason is concrete: `[ApiController]`'s default 400
is a ~200 byte RFC 7807 `ProblemDetails` with a `traceId` — five times the size of the
success response, parsed by a device with 40 KB of heap. Suppressing that needs
`SuppressModelStateInvalidFilter` plus a custom factory; a minimal endpoint just returns
the 40 bytes you meant. Use source-generated `JsonSerializerContext` so output is exact
and reflection-free, and so the frozen contract is visible in one file.

### 5.1 Enrollment

```
POST /d/v1/enroll
{"code":"K7M4-9PQ2","hw":"esp8266-1m","fw":"2026.09.23","mac":"5CCF7F123456",
 "model":"bme280","chip":"00d1a2b3"}

200 {"device_id":"7f3ab2c1-…","token":"kcd1_9f2bQ7xK…","policy":{"sample_s":10,
     "flush_s":60,"max_batch":12,"v":0},"server_ts":1790000351}
```

~181 bytes of response. `mac` is the durable hardware identity; `chip`
(`ESP.getChipId()`) is diagnostic. `hw` is the profile key. `model` decides whether
humidity is ever expected.

Errors carry a tiny body, never `ProblemDetails`: `{"err":"code_invalid"}`,
`code_expired`, `code_used` (409), `bad_request`, `slow_down` (429 + `Retry-After`).

Distinguishing invalid from expired is a deliberate, small information leak, justified by
a 15 minute TTL, single use, 10/min/IP, five failed attempts burning the code, and an
alphabet giving 32⁸ ≈ 1.1×10¹² possibilities against a handful of live codes. The UX gain
on the local page is worth it.

Code format: 8 characters of Crockford base32 minus ambiguous glyphs (no `I`, `L`, `O`,
`U`), rendered `XXXX-XXXX`, stored only as `sha256(normalized)`.

**The code is short-lived on purpose.** The clock's local settings page is plain HTTP with
no authentication (`Clock_ESP01/Clock_ESP01.ino:593-647`) — anyone on the home Wi-Fi can
read it. A long-lived secret must never be typed there. A code that was observed is
already spent or expired. The device exchanges it over TLS for the real credential.

The form field follows the existing OTA password convention exactly: `type=password`, the
value never rendered back, blank meaning *leave as is*
(`Clock_ESP01/Clock_ESP01.ino:612-615`).

**Exchange is one transaction**, and it is also the transfer mechanism:

1. find or create `device` by `hardware_id = mac` — never duplicate, this is what makes
   transfer coherent;
2. close any open binding (`unbound_at = now()`);
3. revoke all credentials for the device;
4. insert the new binding for the code's account;
5. insert `device_access(role='owner')`;
6. 32 CSPRNG bytes → token; store `sha256(token)`;
7. mark the code consumed.

> **Known, accepted exposure.** Because enrollment *is* transfer, anyone with LAN access to
> `http://k-clock.local/settings` can take the clock from its current owner. This matches
> the page's existing threat model — it can already reset Wi-Fi and set the clock — and for
> a desk clock on a home network it is acceptable. But **log it**, and show the previous
> owner "unbound on <date> by re-enrolment" rather than letting the device silently vanish
> from their list. If it ever needs to be harder, add a "release device" step that must
> happen in the cloud before a bound MAC will re-bind: one boolean check in step 2.

### 5.2 Telemetry

```
POST /d/v1/telemetry        Authorization: Bearer kcd1_…
{"seq":4417,"cfgv":0,"samples":[
  {"ts":1790000351,"tc":214,"p":746,"h":41},
  … five more … ]}

200 {"ok":true,"accepted":6,"dup":0,
     "policy":{"sample_s":10,"flush_s":60,"max_batch":12,"v":0}}
```

Request ~285 bytes of body, response **90 bytes**. The v2 shape with `time` and `config`
is ~156 bytes; adding a firmware block reaches ~260. The 512 byte budget holds.

Encoding choices, each for a reason:

- **`ts` is epoch seconds**, not ISO-8601. Ten bytes instead of twenty, and far more
  importantly the ESP formats it with `snprintf("%lu")` rather than dragging in `strftime`
  and `struct tm`. At a 480 KB flash ceiling that matters.
- **`tc` is tenths of a degree as an integer**, exactly as it already crosses the UART.
  No float formatting — the firmware already established that costs over a kilobyte.
- **`h` is omitted entirely on a BMP280 board.** Do **not** propagate the `-1` sentinel
  into the cloud. It will land in an average exactly once and then be a long bug hunt.
- **`seq`** is a monotonic batch counter — reboot detection and gap detection for twelve
  bytes.
- **`cfgv`** is the config version the device holds. In v1 it is always `0` on both sides.
  It exists only so v2's config downlink is a pure addition. Cheapest insurance here.

### 5.3 Status codes, and what the device must do

| Status | Device must |
|---|---|
| `200` | drop those samples from the ring, reset backoff, apply `policy` |
| `400` | **drop the batch.** Never retry a 400 — a poison batch retried forever wedges the ring permanently |
| `401` | stop sending; **keep the token in EEPROM**; show "не прив'язано" locally; re-probe at most hourly |
| `403` / `404` | stop, retry hourly, show the state locally |
| `408`, `5xx`, TLS/DNS failure, timeout | keep the samples, back off 1 → 2 → 5 → 10 → 15 min |
| `413` | halve the batch, retry once, then treat as 400 |
| `429` | honour `Retry-After` exactly |

**The 401 rule is the one most likely to be got wrong.** The tempting implementation is
"401 → erase the token". Do not. A bad deploy or a mis-scoped `WHERE` returning 401 for
everyone would then permanently unenroll the entire fleet, recoverable only by physically
visiting every clock. **The token is erased only when a new one is successfully enrolled.
A 401 is a state, not an event.**

### 5.4 Idempotency and lateness

No `Idempotency-Key` header is needed. `(device_id, ts)` is the natural key and ingest is
a single statement:

```sql
INSERT INTO telemetry (device_id, ts, binding_id, temperature_dc, pressure_mmhg, humidity_pct, …)
SELECT … FROM unnest(@ts::bigint[], @tc::smallint[], @p::smallint[], @h::smallint[]) AS s(…)
ON CONFLICT (device_id, ts) DO NOTHING
```

Idempotent by construction, order-independent, and correct for a device that retried after
a timeout it never saw the answer to. `accepted` is the affected row count,
`dup = submitted − accepted`. Note the **array parameters**: a naive EF `AddRange` of 60
rows generates 360 parameters, and Npgsql's limit is 65 535 — a silly way to find out.

Server-side window, returning 400 only if *every* sample fails:

- reject `ts > now + 120 s`;
- reject `ts < 2026-01-01` (DS3231 never set);
- reject `ts < now − ingest_max_lateness`.

Out-of-window samples inside an otherwise good batch are dropped and counted, so the
device never wedges. Every row also carries `received_at`, so "when the device says it
happened" and "when we heard" stay separable. You will want that the first time an RTC
drifts.

One subtlety worth writing down: if the device's clock steps backwards after a correction,
a later, more accurate sample can collide with an earlier wrong one and `DO NOTHING` keeps
the wrong one. Accepted — `DO UPDATE` would make replay non-idempotent, which is worse.

### 5.5 The two profiles

One API, one contract. The difference is the `policy` block computed from
`device.profile`, plus transport restraint.

| | `esp8266-1m` | `esp32` |
|---|---|---|
| `flush_s` | 60 (tunable to 120 if heap is tight) | 30 |
| `max_batch` | 12 | 60 |
| ring | ~180 samples, RAM | thousands |
| TLS buffers | 16 KB (assume MFLN fails) | 16 KB, don't care |
| session resumption | mandatory | nice to have |

Every `policy` field is overridable **per device** by a nullable column, so one flaky unit
can be slowed down without a deploy and without reflashing. That is the point of
server-controlled cadence and it belongs in v1 even though the UI for it is v2.

---

## 6. Device authentication

**An opaque 32-byte CSPRNG token**, rendered `kcd1_` + base64url ≈ 48 characters, sent as
`Authorization: Bearer`. Stored on the device in EEPROM; stored on the server only as
`sha256(token)` with a unique index.

**Not a JWT.** Three hundred to five hundred bytes on every one of ~1 440 daily requests
and in a 4 KB EEPROM; the device cannot meaningfully validate a signature so it buys
nothing on the wire; and revocation must be prompt, which forces a server-side denylist —
i.e. exactly the database lookup the JWT was meant to avoid. You pay both costs.

**Not mTLS.** Correct for an industrial fleet, wrong here: a client key and certificate in
ESP-01 flash plus an ECDSA signature inside a handshake budget that is already tight; you
would have to run a CA and a revocation path; Caddy terminating client certs moves the
authorization decision into the proxy, so revoking a device means rewriting a CRL and
reloading rather than one `UPDATE`; and enrollment-by-code would become a CSR flow — a lot
of ASN.1 on a device with no outbound HTTP client at all today. Revisit at thousands of
units with a support obligation, not before.

**Hash with SHA-256, not bcrypt/argon2.** The reflex is "hash credentials slowly", and it
is wrong here: the token is 256 bits of CSPRNG output, so there is no offline search to
slow down. A slow KDF buys nothing and costs 50–200 ms of CPU on every batch. Likewise,
the lookup is a btree probe on a hash and does **not** need to be constant time — say so
in a comment, or someone will "fix" it into a full scan plus `FixedTimeEquals`.

Verification is one indexed query joining credential → device → binding, filtered on
`revoked_at IS NULL AND unbound_at IS NULL`. **No cache in v1**: 100 devices is 1.7
lookups/s, and a cache is precisely what would make revocation take until TTL instead of
effect-on-next-request. Keep it in one method so adding `IMemoryCache` later is a one-file
change.

Revocation happens in the same transaction that closes the binding and **takes effect on
the device's next request** — worst case one `flush_s`. No background job.

---

## 7. Data model

### Ownership is a period

The naive `telemetry(device_id, …)` plus `device.owner_id` violates the transfer
requirement by construction. The composite key `(owner_id, device_id, ts)` was considered
and rejected:

- a **primary key does not constrain access** — `WHERE device_id = 42` returns other
  people's rows whatever the key contains; a predicate constrains access, not a key;
- it **weakens** what the key is for: natural uniqueness is `(device_id, ts)`, and putting
  the owner in the key *permits* two rows for one instant under different owners —
  precisely the hazard while a transfer is in flight and the device is draining its ring;
- `(owner, device)` is **not unique over time** (A → B → A merges two distinct episodes);
- sharing breaks the simple comparison: "row owner == token subject" holds only while
  exactly one person sees the device, and the Viewer role reintroduces the indirection;
- an unbound device's `owner_id` is NULL, which a primary key forbids.

So: **PK stays `(device_id, ts)`, and `binding_id` rides along as an ordinary NOT NULL
column.** Same denormalisation the composite key was reaching for, at the right grain.

```sql
CREATE TABLE account (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    email        citext, display_name text,
    created_at   timestamptz NOT NULL DEFAULT now(),
    deleted_at   timestamptz);

CREATE TABLE external_login (
    provider text NOT NULL, subject text NOT NULL,
    account_id uuid NOT NULL REFERENCES account(id) ON DELETE CASCADE,
    PRIMARY KEY (provider, subject));

CREATE TABLE device (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    hardware_id  text NOT NULL UNIQUE,      -- STA MAC, normalised
    profile      text NOT NULL,             -- esp8266-1m | esp8266-4m | esp32
    model        text,                      -- bme280 | bmp280
    policy_sample_s int, policy_flush_s int, policy_max_batch int,  -- per-device override
    created_at   timestamptz NOT NULL DEFAULT now());

CREATE TABLE device_binding (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    device_id  bigint NOT NULL REFERENCES device(id),
    account_id uuid   NOT NULL REFERENCES account(id),
    display_name text, timezone text,
    bound_at   timestamptz NOT NULL DEFAULT now(),
    unbound_at timestamptz, unbound_reason text);

CREATE UNIQUE INDEX device_binding_active
    ON device_binding (device_id) WHERE unbound_at IS NULL;

CREATE TABLE device_access (
    binding_id bigint NOT NULL REFERENCES device_binding(id) ON DELETE CASCADE,
    account_id uuid   NOT NULL REFERENCES account(id) ON DELETE CASCADE,
    role       text   NOT NULL,             -- owner now, viewer later
    revoked_at timestamptz,
    PRIMARY KEY (binding_id, account_id));
```

`device_access` keys on the **binding**, not the device. That is what makes transfer safe:
closing a binding closes every access granted under it, with no separate cleanup. It is
also why a Viewer role later is an `INSERT` and not an authorization rewrite.

`device.id` is a narrow `bigint` because it repeats in every telemetry row and takes part
in `compress_segmentby`. `account.id` is a `uuid` because it reaches tokens and URLs where
sequential integers are unwelcome.

`model` is a **property of the device, not of a sample**, because on the wire humidity
`-1` does not distinguish "BMP280 board, no sensor" from "BME280 did not answer"
(`Clock_Arduino/Clock_Arduino.ino:928-932`). The board variant is reported once at
enrollment; the server never guesses it from a batch.

```sql
CREATE TABLE enrollment_code (
    code_hash  bytea PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES account(id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    attempts   int NOT NULL DEFAULT 0,
    consumed_at timestamptz, consumed_device_id bigint);

CREATE TABLE device_credential (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    device_id  bigint NOT NULL REFERENCES device(id),
    binding_id bigint NOT NULL REFERENCES device_binding(id) ON DELETE CASCADE,
    token_hash bytea NOT NULL UNIQUE,
    issued_at  timestamptz NOT NULL DEFAULT now(),
    revoked_at timestamptz, revoked_reason text);
```

The credential is issued **per binding**. So `binding_id` for a telemetry row comes
straight off the authenticated token: no extra lookup, and no way to attribute a row to
the wrong owner.

### Telemetry

```sql
CREATE TABLE telemetry (
    device_id       bigint      NOT NULL,
    ts              timestamptz NOT NULL,
    binding_id      bigint      NOT NULL,
    temperature_dc  smallint,                -- tenths of a degree
    pressure_mmhg   smallint,
    humidity_pct    smallint CHECK (humidity_pct BETWEEN 0 AND 100),
    wifi_rssi_dbm   smallint,
    esp_free_heap_b int,
    esp_uptime_s    int,
    link_good       int, link_dropped int, link_rejected int,
    received_at     timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (device_id, ts));

SELECT create_hypertable('telemetry', 'ts',
       chunk_time_interval => INTERVAL '7 days', create_default_indexes => false);
```

The firmware's sentinels (`-999` temperature, `-1` pressure, `-1` humidity) become `NULL`
**at ingest, in one place**. Storing them is not an option: `-999` is also a legal
`-99.9 °C`, and any future `avg()` would be quietly wrong. The `CHECK` on humidity means
the sentinel cannot get in even by accident.

Firmware versions do **not** sit in every row — they change rarely. They live in
`device_state(device_id PK, last_ingest_at, last_seq, esp_version, nano_version, …)`,
which is deliberately a **separate table from `device`**: `device` is cold and read by
every browser request, `device_state` is written on every batch. Keeping them apart keeps
the hot write off the row the UI reads.

### Compression and aggregates

```sql
ALTER TABLE telemetry SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'device_id, binding_id',
    timescaledb.compress_orderby   = 'ts DESC');
SELECT add_compression_policy('telemetry', INTERVAL '2 days');
```

`compress_segmentby` earns its keep three separate times: query pruning, GDPR erasure
touching only one binding's batches instead of rewriting every chunk, and correct
per-owner aggregates. Do not let anyone "simplify" it to `device_id` alone.

**The compression delay has a hard floor**, and this is the most important constraint in
the whole schema:

```
device ring buffer (30–60 min)  <  ingest_max_lateness (2 h)  <<  compression delay (2 days)
```

A replayed batch must never land in an already-compressed chunk. Put
`ingest_max_lateness` in configuration in one place and derive the rest from it.

```sql
CREATE MATERIALIZED VIEW telemetry_1h
WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
SELECT device_id, binding_id, time_bucket(INTERVAL '1 hour', ts) AS bucket,
       avg(temperature_dc)::smallint AS temp_avg,
       min(temperature_dc) AS temp_min, max(temperature_dc) AS temp_max,
       avg(pressure_mmhg)::smallint AS pressure_avg,
       avg(humidity_pct)::smallint  AS humidity_avg,
       count(*) AS samples
FROM telemetry
GROUP BY device_id, binding_id, bucket
WITH NO DATA;
```

**`binding_id` must be in the `GROUP BY`, and this must be decided in the v1 migration
even though charts are v2.** An aggregate grouped only by `(device_id, bucket)` silently
averages two owners' readings together across a transfer, and fixing it means dropping and
re-materialising the whole view. This is the quietest possible bug in the system and the
most expensive.

`WITH NO DATA` is not optional: `WITH DATA` backfills the entire history inside the
migration, and there is a known failure executing it inside an extended-protocol pipeline
— which is how Npgsql talks to the server.

Retention exists and is **disabled**, with a deliberately absurd interval so that anyone
re-enabling it "to see what it does" still does no harm:

```sql
DO $$ DECLARE j int;
BEGIN
  SELECT add_retention_policy('telemetry', INTERVAL '100 years') INTO j;
  PERFORM alter_job(j, scheduled => false);
END $$;
```

There is a real, non-obvious cost to keeping raw data forever, and it is not storage:
**turning retention on freezes the aggregate definitions**, because adding a column to an
aggregate means re-materialising from raw. Keep retention off until the schema has
settled.

---

## 8. Isolation — and the blocker to check first

> ⚠️ **Verify before writing the migration.** TimescaleDB appears to refuse row-level
> security and columnstore/compression on the same hypertable, in both directions:
> enabling RLS on a compressed hypertable errors, and enabling compression on a table with
> RLS errors. If that holds, **the plan of "just put a policy on `telemetry`" does not
> work at all**, and the workaround below is mandatory rather than optional. Confirm on
> the actual `timescale/timescaledb-ha:pg17` image, in an hour, before anything is built
> on top. Relevant upstream issues: timescale/timescaledb#6827, #7830, #5787.

### The design that survives either answer

- **RLS stays on the ordinary tables** — `account`, `device`, `device_binding`,
  `device_access`, `enrollment_code`, `device_credential`. These are not hypertables and
  have no columnstore, so nothing is in tension.
- **Telemetry is reached only through a `security_barrier` view and `SECURITY DEFINER`
  functions.** The application role holds **zero** privileges on the hypertable itself, so
  there is no query it can write that reaches a row directly. The view joins through
  `device_access` to the current account.
- **Continuous aggregates need their own protection regardless.** Timescale materialises
  them in a background job running as the *table owner*, which is exempt from RLS — so the
  materialised hypertable behind the aggregate holds every account's data. Give the
  aggregate its own barrier view with the same predicate.

### The session variable, and the two traps

Identity reaches the database as a GUC:

```csharp
await db.Database.ExecuteSqlAsync(
    $"SELECT set_config('app.account_id', {accountId.ToString()}, true)");
```

**Trap one: use `set_config(name, value, true)`, never `SET LOCAL`.**
`SET LOCAL app.account_id = $1` is a *syntax error* — `SET` does not take parameters. The
moment someone finds that out, the obvious next step is string concatenation, and now
there is SQL injection in the tenancy boundary. `set_config` is an ordinary function call
and is parameterisable.

**Trap two: `SET` (without `LOCAL`) is session-scoped and Npgsql pools connections.** A
GUC set for request A survives on the pooled connection into request B for a different
account. That is a cross-tenant leak and it is the single most dangerous bug this design
can produce. `SET LOCAL`/`is_local = true` is transaction-scoped and safe — **but only if
there is a transaction**, and while `SaveChangesAsync` opens one implicitly, `ToListAsync`
does not. Most of the browser API is reads.

Therefore: **an explicit transaction per authenticated request**, opened by middleware on
the `/api` group, which sets the GUC, calls the endpoint, and commits or rolls back. At
this scale the cost is irrelevant — but set `Max Pool Size=20` explicitly in both
connection strings, because the default of 100 times two contexts times two replicas is
400 connections against a `max_connections` of 100.

`current_setting('app.account_id', true)` — the `true` is `missing_ok`, so an unset GUC
yields NULL, the predicate is false, and the result is **zero rows** rather than an
exception or, catastrophically, everything. There must be an explicit test for all three.
Any helper function reading it must be `STABLE`, never `IMMUTABLE`.

### Also add EF global query filters

Belt and braces, and each reason is practical: the generated SQL then *reads* correctly in
logs, so "why is this empty" is answerable without `EXPLAIN` and `pg_policies`; `Include()`
across a device you do not own returns empty rather than a truncated graph; and if the GUC
is ever unset by a new code path, the filter fails at the application layer where the
stack trace is useful, with the database as the backstop.

### Three roles

| Role | Purpose | RLS |
|---|---|---|
| `clock_migrator` | owns the schema, runs DDL | owner, exempt |
| `clock_app` | browser API | policies apply; **no privileges on `telemetry`** |
| `clock_ingest` | writes telemetry, reads credentials | permissive policies |

Use **permissive policies for the ingest role, not the `BYPASSRLS` attribute**:
`BYPASSRLS` needs superuser to grant — available in a container today, not on any managed
Postgres you might move to — and a policy is visible in `pg_policies`, where you will look
in a year.

Do **not** set `FORCE ROW LEVEL SECURITY`: the owner must stay exempt or migrations break,
and Timescale's background workers run as the owner and would break too.

And never `ALTER DEFAULT PRIVILEGES ... GRANT SELECT ... TO clock_app` — it would silently
grant read on `telemetry` the instant the migration creates it. Grant table by table, and
review the list.

---

## 9. Transfer of ownership

```
1. owner presses Unbind
     UPDATE device_binding SET unbound_at = now() WHERE id = :b AND unbound_at IS NULL;
     UPDATE device_credential SET revoked_at = now() WHERE device_id = :d AND revoked_at IS NULL;
2. clock gets 401 -> stops sending, KEEPS the token, shows "не прив'язано"
3. new owner issues a code, types it into the local page -> new binding, new credential
```

The new owner cannot see the old data because the old rows carry a different `binding_id`
and `device_access` is keyed on the binding. It is not a filter someone remembered to
write; it is a consequence of the key. Control, separately, requires
`role = 'owner' AND unbound_at IS NULL`, so the previous owner can read their history but
cannot rename, reconfigure or transfer the clock.

### The in-flight sample — the definite answer

**Attribute a sample to the binding whose period contains the sample's own timestamp. Do
not reject it.** A reading stamped 14:58 that arrives at 15:04, after an unbind that
committed at 15:00, was physically taken in the previous owner's home while they still
owned the clock. It is their data. Rejecting it would punch a hole in the last minutes of
someone's record for no reason but a slow network.

The join is half-open — `ts >= bound_at AND (unbound_at IS NULL OR ts < unbound_at)` — so
the instant of the unbind belongs to the new binding and never to both, and the partial
unique index guarantees at most one match.

Three things make that rule safe rather than exploitable:

- **A lateness bound is required.** Whoever holds the clock holds its RTC, and the
  firmware's programming screens will accept nonsense hours. Without an upper bound, a new
  owner could wind the clock back and inject rows into the previous owner's history — they
  could not read them, but they could pollute a record someone is entitled to have be
  accurate. `ingest_max_lateness` of 2 hours closes a binding to new rows well before that
  is reachable, and comfortably after the ring buffer has drained.
- **In practice the window is one request, not two hours**, because step 1 revokes the
  credential and the next POST fails at authentication. The timestamp rule covers only the
  request already on the wire.
- **Samples matching no binding are counted and discarded, and the request still returns
  200**, so the device clears its ring instead of retrying until the heap gives out. Never
  store them with a NULL binding: that is data nobody is entitled to, nobody consented to,
  and nothing will ever delete. `NOT NULL` makes the mistake unmakeable.

A tempting variant — keep the credential valid for `ingest_max_lateness` so the ring
drains cleanly — is **rejected**: it means a clock already in its new home keeps reporting
into the old owner's account for two hours. Security wins; the samples lost are the ones
taken while the device was being unplugged.

---

## 10. Account deletion

Telemetry is the temperature, pressure and humidity inside a named person's home, sampled
every ten seconds. It is personal data by linkage; deleting `device_access` and leaving the
rows is pseudonymisation, not anonymisation. The rows have to go.

**Phase 1, synchronous, inside the request:** scrub `account` to a tombstone, delete
`external_login` (severing the Google identity so nobody can log back in), close all
bindings, revoke all credentials, enqueue the ranges into `purge_queue`, delete
`device_access`. After this commits, **no live path reaches the telemetry**, and the
"without undue delay" clock is satisfied. It is cheap and bounded.

**Phase 2, background, metered:** delete from the hypertable one `(binding, chunk)` at a
time, oldest first, checkpointing a cursor so a restart resumes rather than repeats.

Why phase 2 is expensive: a `DELETE` on a compressed chunk cannot delete a row, because
there are no rows. Timescale must decompress the matching batches into the chunk's heap,
delete there, and leave the chunk partially compressed until a recompression pass rebuilds
it — roughly an order of magnitude more I/O, plus a transient *increase* in database size
at the exact moment you were trying to reduce it. Three years of history is 156 chunks at
a 7-day interval. `compress_segmentby = 'device_id, binding_id'` is what keeps this
survivable: the predicate is exactly the segment key, so every other device in each chunk
stays compressed and untouched.

**The step everyone forgets:** deleting raw rows does **not** remove the corresponding
aggregate buckets. It records an invalidation that only a refresh covering it will act on
— and for old data the refresh policy never will. So the purge job must call
`refresh_continuous_aggregate(…, from, to)` for each purged range, which zeroes that
binding's buckets while leaving every other device's buckets in the same hours correct,
precisely because `binding_id` is in the `GROUP BY`. Then recompress. Neither refresh can
run inside a transaction block.

**Backups.** You cannot edit a row out of a base backup. The defensible practice is a
short, published retention (say 30 days), an explicit statement that erasure reaches
backups by expiry rather than by edit, and a runbook step that re-applies pending erasures
to anything restored. Write that step now, while you remember why it exists.

---

## 11. Sizing

Per device-year at 10 s cadence: **3.15 M rows**, ~84 bytes of heap tuple plus ~28 bytes of
PK index ≈ **350–400 MB raw**. Compressed, the column encodings suggest ~4 B/row ≈ 13 MB,
but that is a floor — **plan on 20–30 MB per device-year**, a 12–18× ratio.

| devices | rows/s | raw/day | compressed/year | 10 years |
|---|---|---|---|---|
| 10 | 1 | 10 MB | 250 MB | 2.5 GB |
| 100 | 10 | 97 MB | 2.5 GB | 25 GB |
| 300 | 30 | 291 MB | 7.5 GB | 75 GB |

**At a few hundred devices, keeping everything forever costs about 8 GB a year.** The
headline is genuinely reassuring.

`wifi_rssi_dbm` and `esp_free_heap_b` are the two most expensive columns — together about
a third of the compressed size — because they are genuinely noisy. Still a good trade for
diagnostics; if the measured figure comes back much worse, sampling RSSI once a minute
rather than every sample costs nothing diagnostically.

**Everything above is arithmetic, not measurement.** After two weeks of real data, believe
`hypertable_compression_stats()` and `chunk_compression_stats()` instead.

### What hurts first, honestly

1. **The uncompressed hot window stops fitting in page cache** — at roughly 300 devices on
   an 8 GB VPS, i.e. the top of the stated range, not somewhere comfortably beyond it. The
   symptom is ingest latency climbing while CPU stays flat and a supposedly write-only
   workload shows read I/O. The fix is shorter chunks and a shorter compression delay —
   but that delay cannot go below the ring buffer plus the lateness budget without
   reintroducing the replay-into-compressed-chunk problem. That tension is real and it is
   the reason the ring buffer is short.
2. **Transaction rate**, showing up as connection-pool sizing long before CPU.
3. **The read path**, if the barrier view fails to push time bounds down — every chart
   query degrades from one chunk to every visible binding's full history. Invisible at 10
   devices with a month of data; very visible at 300 with three years.
4. **Chunk count**, around 10 000.
5. **Disk. Last, not first** — which is the counterintuitive part.

**The first bottleneck overall is not the database at all: it is TLS handshakes.** At 1.7
handshakes/s that is fine, but it is roughly 100× the CPU of the insert it protects. Caddy
on one core saturates somewhere around 2 000–5 000 devices; the database would not notice
until ~50 000.

---

## 12. Solution layout

```
Server/
  KClock.sln
  Directory.Build.props              net10.0, nullable, warnings-as-errors
  src/KClock.Api/                    the only deployable
    Program.cs                       one composition root, everything visible
    AppJsonContext.cs                source-gen context — the frozen wire format
    Devices/                         enroll, telemetry, DeviceAuthenticationHandler, policy
    Accounts/                        google auth, /api/devices, /api/enrollment-codes
    Infrastructure/                  AccountScopeMiddleware, ResponseBudget filter
  src/KClock.Data/                   entities, DbContext, migrations, the SQL
  tests/KClock.Tests/                xUnit + Testcontainers + WebApplicationFactory
  tools/KClock.FakeDevice/           console harness that pretends to be a clock
  compose.yaml  Caddyfile  db/init/00_bootstrap.sql  .env.example
```

### What deliberately does not exist

This is a few hundred desk clocks. The following are all reflexes, and all wrong here: a
`Domain`/`Application`/`Infrastructure` split (there is no domain logic — there is a table
and an HTTP handler); MediatR, CQRS, AutoMapper; an `IDeviceRepository` over `DbContext`
(the `DbContext` *is* the repository, and wrapping it is what makes the account-scope
middleware hard to reason about later); Redis, a broker, a queue, gRPC; any interface with
one implementation existing "for testing", since the tests here run against a real
Postgres. **If a file's only content is forwarding a call to another file, it should not
exist.**

Two production projects. `KClock.Data` is split out only because the migrations and the
raw SQL defining the hypertable, the policies and the grants are the highest-risk artifact
in the system, and they should be importable by the tests without dragging in the web
host's startup side effects. Do not go to three.

`.gitignore` needs a .NET section — `bin/`, `obj/`, `*.user`, `.env`,
`**/appsettings.*.local.json`. Note that `Debug/`, `Release/`, `*.map`, `*.o` and `*.a` are
already ignored for the Atmel builds, but `bin/` and `obj/` are not.

### Browser API (v1)

`GET /auth/google/start`, `GET /auth/google/callback`, `POST /api/auth/signout`,
`GET /api/me`, `GET /api/devices`, `GET /api/devices/{id}`, `PATCH /api/devices/{id}`,
`DELETE /api/devices/{id}/binding`, `POST|GET|DELETE /api/enrollment-codes`.

Two modelling points that matter:

- **There is no `DELETE /api/devices/{id}`.** The `device` row is global, outlives every
  binding, and is keyed on a MAC that physically exists. What a user deletes is *their
  binding*. Deleting the device row breaks transfer and orphans the previous owner's
  telemetry.
- `online` is `last_ingest_at > now() − 3 × flush_s` — three missed reports, deliberately
  the same shape as the firmware's own `LinkStaleMs` (3 polls × 3 s).

**Session: cookie, `HttpOnly` + `Secure` + `SameSite=Lax`.** Not a JWT in `localStorage`.
The decision that makes this cheap is **serving the SPA from the same origin** via Caddy,
which removes CORS, token storage, XSS exfiltration and refresh rotation — about a week of
plumbing. Reconsider only if the SPA must live on another origin. CSRF: `SameSite=Lax`
already blocks cross-site POSTs; add an endpoint filter requiring `X-Requested-With` on
non-GET `/api/*`, which cannot be set cross-origin without a preflight that fails. Three
lines, same guarantee as antiforgery tokens with fewer moving parts.

### Migrations

| Owned by | What |
|---|---|
| `db/init/00_bootstrap.sql`, superuser, once | extensions, roles, schemas |
| EF model + generated migration | tables, columns, PKs, FKs, plain indexes |
| `migrationBuilder.Sql(...)` | `create_hypertable`, compression, policies, partial indexes |
| `Sql(..., suppressTransaction: true)` | continuous aggregates |
| `Sql(...)`, always last | views, `SECURITY DEFINER` functions, RLS policies, grants |

Ordering that is not negotiable: bootstrap first; `CREATE TABLE telemetry` and
`create_hypertable` in the *same* migration before any row exists (converting a populated
table takes heavy locks); the PK before `create_hypertable`; compression settings after
every index you want; the daily aggregate after the hourly one it reads.

`CREATE MATERIALIZED VIEW ... WITH (timescaledb.continuous)` cannot run inside a
transaction block, so such a migration is **not atomic** — keep aggregate migrations small
and single-purpose, and write `Down` methods that cope with a half-applied state.

Run migrations as `clock_migrator` from a **one-shot container** via
`dotnet ef migrations bundle`, with the API depending on
`service_completed_successfully`. **Never `Database.Migrate()` on startup**: the app role
has no DDL rights and replicas would race.

**Rule for every future telemetry column: nullable, no default. Always.** `ADD COLUMN` on
a hypertable with compressed chunks is fine for a nullable column with no default; add
`NOT NULL DEFAULT` and you may force a decompression of the entire history — a multi-hour
rewrite triggered by a one-line migration that looked harmless in review. Put this in the
PR template.

### Ops

Structured JSON logs to stdout with `device_id`, `binding_id`, `account_id`, `seq`;
`/health/live` (no DB) and `/health/ready` (with DB), the latter **bypassing the account
scope middleware**; `/metrics` for Prometheus with Grafana in compose — which also means
per-device charts exist before the SPA does. `pg_dump` in cron, and the restore procedure
written down and tested once, because a Timescale restore needs the extension present and
`timescaledb.restoring = 'on'`.

Three deploy-day papercuts whose symptoms point the wrong way:

1. **Data Protection keys** default to a path inside the container and are destroyed on
   every redeploy, signing every user out. Persist them to a named volume with an explicit
   `SetApplicationName`. The symptom reads as an auth bug and is not.
2. **Forwarded headers.** Behind Caddy, `Request.Scheme` is `http`, so the Google
   `redirect_uri` is built as `http://` and Google rejects it. `UseForwardedHeaders` must
   be the *first* middleware, with `KnownNetworks`/`KnownProxies` cleared or set to the
   Docker subnet — otherwise it silently ignores the headers.
3. **Caddy `encode` must exclude `/d/*`**, and `key_type p256` must be set for the ECDSA
   chain. Both look like noise and are load-bearing.

---

## 13. Verification

No hardware in the loop. Three things pay for themselves, in build order.

**Isolation and transfer** — against a real `timescaledb-ha` container via Testcontainers.
This suite cannot be replaced by careful reading, because with a single account a policy
that leaks everything and a correct one behave identically. The transfer test is cheap and
validates the whole ownership model at once: account A enrolls a device, writes five
samples, account B enrolls the same MAC, writes five more; assert each sees exactly five
and the sets are disjoint. Plus: unset GUC returns zero rows and does not throw; only one
open binding per device is enforced by the database; the hourly aggregate does not leak.

**The device contract as a characterization test** — `WebApplicationFactory` posting the
literal bytes the firmware will send, kept as fixtures so a diff shows a contract change
*as* a contract change. Assert the **exact response bytes** on the happy path, and walk
every `/d/*` endpoint asserting the size budget and the absence of `Content-Encoding`.
That is what stops a careless `[JsonPropertyName]` rename from bricking a fleet. Cover:
duplicate batch, out-of-order batch, future timestamp, all-samples-out-of-window, revoked
token, BMP280 batch storing NULL, re-enrollment closing the old binding.

**`tools/KClock.FakeDevice`** — ~150 lines, not a test project but a tool: takes a code and
a URL, samples, flushes, drops batches at random, reboots (resetting `seq`, keeping the
token), skews its clock, honours `policy`, and implements the §5.3 table including the 401
rule. This is what lets the server be finished and trusted before the firmware has an HTTP
client at all, and it is where the backoff and 401 semantics will turn out to be wrong.

What not to test: endpoint handlers with a mocked `DbContext` (the interesting behaviour is
in the SQL), EF mappings, a coverage number.

**The gap that cannot be closed in-process** is TLS behaviour. Substitute
`Server/scripts/tls-check.sh` against the real deployment: `openssl s_client -tlsextdebug`
to confirm the chain is leaf plus one intermediate and both ECDSA; `-reconnect` to confirm
session resumption actually happens; `-maxfraglen 512` to confirm whether MFLN is honoured
(expect not); and a `curl` pinned to the exact cipher BearSSL will choose. Run it after
every Caddy change.

After the first deploy, one query is the best five-second health check this design has:

```sql
SELECT job_id, proc_name, owner, scheduled, config
FROM timescaledb_information.jobs ORDER BY job_id;
```

Expect a compression job scheduled `true`, a retention job scheduled **`false`**, and the
aggregate refresh jobs scheduled `true`.

---

## 14. Build order

1. **Measure ESP-01 free heap and attempt one real TLS handshake.** One hour, gates
   everything (§4).
2. **Verify the RLS × columnstore interaction** on the real image (§8). One hour, decides
   the shape of the whole data layer.
3. `KClock.Data`: entities, first migration, hypertable, roles, grants, barrier views, the
   disabled retention policy, the aggregate definition with `binding_id` in the `GROUP BY`.
4. Isolation and transfer tests. Nothing else validates step 3.
5. Device endpoints, `DeviceAuthenticator`, contract tests.
6. `KClock.FakeDevice` and a local compose stack; run a simulated fleet for a day.
7. Google sign-in, `/api/me`, `/api/devices`, `/api/enrollment-codes`.
8. compose, Caddyfile, deploy, `tls-check.sh`.
9. **Only then**, and as a separate decision because it is serial firmware:
   `Clock_ESP01.ino` — heap measurement, BearSSL with session resumption, EEPROM grown to
   `KCL3` for the token, host and cached config, the ring buffer, and the enrollment-code
   field on the settings page.

---

## 15. Things not to let through review

1. RLS enabled directly on the telemetry hypertable, if §8's blocker is confirmed.
2. A continuous aggregate without `binding_id` in the `GROUP BY`.
3. `SET` instead of `set_config(..., true)`, or either outside an explicit transaction.
4. A helper reading the account GUC marked `IMMUTABLE` rather than `STABLE`.
5. Storing `-1` for humidity instead of `NULL`.
6. `device_access` keyed on `device_id` instead of `binding_id`.
7. `ALTER DEFAULT PRIVILEGES ... GRANT SELECT ... TO clock_app`.
8. A compression delay shorter than the ring buffer plus the lateness budget.
9. A telemetry column added `NOT NULL DEFAULT`.
10. Firmware that erases its token on a 401.
11. `Content-Encoding` or chunked transfer on any `/d/*` response.
12. Any sizing figure in §11 treated as measured rather than estimated.
13. A third production project.
