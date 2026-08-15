# Immanuel.KeyValue v2

The key-value service rebuilt on ASP.NET Core 10, storing each app key in its own SQLite
database through [Ark.Rapid.Database](https://www.nuget.org/packages/Ark.Rapid.Database).

Every v1 endpoint still works at the same URL with the same response format. Nothing written
against the old service needs to change.

---

## Run it

**F5 / Run and Debug** — pick a configuration from the dropdown:

| | |
|---|---|
| **Web (v2)** | Builds, starts the API on `http://localhost:5086`, opens the landing page |
| **Migrator (dry run)** | Reports what a migration would do; writes nothing |
| **Migrator (write)** | Migrates into the web app's `App_Data` |

These live in [`.vscode/launch.json`](../.vscode/launch.json) and work from the repository root.
Visual Studio and Rider use the `Properties/launchSettings.json` profiles instead — same ports.

From a terminal:

```bash
dotnet run --project src/Immanuel.KeyValue.Web    # http://localhost:5086
dotnet test                                        # 95 tests
dotnet build                                       # whole solution
```

Databases are created under `src/Immanuel.KeyValue.Web/App_Data/`.

### Local settings

Each project has an `appsettings.Development.json.example`. Copy it to
`appsettings.Development.json` and edit — **that file is git-ignored**, which is where anything
resembling a credential belongs. The web app runs fine without one; the migrator needs the
connection string from somewhere.

```bash
cp src/Immanuel.KeyValue.Migrator/appsettings.Development.json.example \
   src/Immanuel.KeyValue.Migrator/appsettings.Development.json
```

---

## How storage works

```
App_Data/
  _catalog.db      Registry of issued app keys + a cached key count for each.
  3cg7aby9.db      One database per app key. The file name is the app key.
  pk4m2xn8.db
  ...
```

v1 kept every user's rows in one SQL Server table, so all traffic contended on one set of
locks. In v2 an app key's reads and writes touch only that app key's file, and every file is in
WAL mode so readers never block on a writer.

The one shared file is `_catalog.db`. It is deliberately kept out of the hot path: reads never
touch it, overwrites never touch it, and `LastAccessAt` is stamped at most once per app key
every five minutes. Only issuing a key, or adding/removing one, writes to it.

**The app key is a file name, so it is validated as a security boundary.** Exactly eight
characters of `a-z0-9`, checked before anything reaches the file system — that rules out `../`,
absolute paths, and the `_catalog` name itself. See `AppKey.cs` and its tests.

### Schema

Per app key:

```sql
CREATE TABLE KeyVal (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    KeyName   TEXT    NOT NULL,
    KeyVal    TEXT    NULL,
    IpAddr    TEXT    NULL,
    Agent     TEXT    NULL,
    CreatedAt TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ','now')),
    UpdatedAt TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ','now'))
);
CREATE UNIQUE INDEX IX_KeyVal_KeyName ON KeyVal (KeyName);
```

`ClientKey` is gone — the file name carries it. Timestamps are ISO-8601 UTC text, so they stay
readable in any SQLite browser and carry no time-zone ambiguity.

The two v1 stored procedures became single statements:

| v1 | v2 |
|---|---|
| `sp_UpdateKeyVal` (T-SQL `MERGE`) | `INSERT ... ON CONFLICT(KeyName) DO UPDATE` |
| `sp_UpdateAction` (guarded `UPDATE`) | One `UPDATE` with a GLOB numeric guard |
| `sp_SelectKeyVal` | `SELECT` |

Both remain single statements on purpose: fifty simultaneous increments land on fifty, not
somewhere below it. There is a test for exactly that.

---

## API

### v1 — unchanged

| Method | Path | |
|---|---|---|
| `GET` | `/api/KeyVal/GetAppKey` | Issue an app key |
| `GET` | `/api/KeyVal/GetValue/{appkey}/{key}` | Read, or `""` |
| `POST` | `/api/KeyVal/UpdateValue/{appkey}/{key}/{value}` | Create or overwrite |
| `POST` | `/api/KeyVal/ActOnValue/{appkey}/{key}/{action}` | `increment` / `decrement` |
| `GET` | `/api/KeyVal/GetCount` | Keys stored service-wide |
| `GET` | `/api/KeyVal/GetIp` | Caller's IP |

These return JSON exactly as Web API 2 did — a string response is `"quoted"`. ASP.NET Core
would default to `text/plain`, which would break anyone calling `JSON.parse` on the body, so
the controller forces JSON. There is a test asserting the quotes.

### v2 — for new code

| Method | Path | |
|---|---|---|
| `POST` | `/api/v2/appkeys` | Issue an app key |
| `GET` | `/api/v2/appkeys/{appkey}` | Key count, timestamps |
| `GET` | `/api/v2/appkeys/{appkey}/keys` | List everything stored |
| `GET` | `/api/v2/appkeys/{appkey}/keys/{key}` | Read one key |
| `PUT` | `/api/v2/appkeys/{appkey}/keys/{key}` | Set from body `{"value":"..."}` |
| `DELETE` | `/api/v2/appkeys/{appkey}/keys/{key}` | Remove a key |
| `POST` | `/api/v2/appkeys/{appkey}/keys/{key}/increment` | Step by `{"by":n}` |
| `GET` | `/api/v2/stats` | Service-wide totals |

Listing, deleting, stepping by more than one, and values that contain slashes or newlines are
all new. Failures are RFC 9457 problem documents. Schema at `/openapi/v1.json`, health at
`/health`.

Both APIs read and write the same data — you can mix them freely.

---

## Behaviour changes

Four, all deliberate:

1. **Unknown app keys are rejected.** v1 had no registration: any 8-character string became a
   real store on first write, which meant unbounded file creation here. Keys must now be issued
   first. Set `KeyValue:AutoCreateUnknownAppKeys` to `true` for the old behaviour.
2. **Incrementing a key that doesn't exist creates it at zero**, then applies the step. v1
   silently did nothing, so a counter never started unless you remembered to seed it.
3. **`UpdateValue` reports failure.** v1 always answered `true`. It still answers `true` on
   success, but a rejected write is now a 400/404/409 rather than a cheerful lie.
4. **`ActOnValue` rejects unknown actions** with a 400 instead of reporting success. `increment`
   and `decrement` both work; `decrement` is new, though the v1 front page always advertised it.

App keys are now generated with a cryptographic RNG rather than a shared `System.Random`, so
issued keys are no longer predictable. Existing keys are unaffected.

---

## Configuration

`appsettings.json`, or environment variables like `KeyValue__DataDirectory`.

```jsonc
{
  "KeyValue": {
    "DataDirectory": "App_Data",       // Relative to the content root, or absolute
    "MaxKeyLength": 64,                // v1's varchar(64)
    "MaxValueLength": 1024,            // v1's varchar(1024)
    "MaxKeysPerAppKey": 1000,          // New: stops one key filling the disk
    "AutoCreateUnknownAppKeys": false  // true restores v1's permissive writes
  },
  "RateLimit": {
    "Enabled": true,
    "PermitsPerMinute": 300,           // Per client IP
    "QueueLimit": 0
  },
  "Proxy": {
    "Enabled": true,
    "TrustAllProxies": false,          // See the warning below
    "KnownProxies": []
  }
}
```

### Deploying behind a reverse proxy

Two settings need attention:

- **`Proxy`** decides how much to believe `X-Forwarded-For`, which determines both the IP
  recorded against a write and the key the rate limiter counts against. Leaving
  `TrustAllProxies: false` and listing your proxy in `KnownProxies` is the safe choice —
  `TrustAllProxies: true` lets anyone who can reach the app directly forge their address and
  slip the rate limiter. Only set it when nothing but your proxy can reach the app.
- **TLS terminates at the proxy.** The app deliberately does not call `UseHttpsRedirection()`;
  doing so behind a TLS-terminating proxy is the usual cause of redirect loops.

`DataDirectory` should point at persistent storage — it is live user data. Back it up like a
database, because it is one. The `.gitignore` keeps `App_Data/` and `*.db` out of the repo.

---

## Migrating from v1

The tool reads the v1 SQL Server table and fans each `ClientKey` out into its own SQLite file.
It is safe to re-run: existing values are left alone unless you pass `--overwrite`.

Put the connection string in `src/Immanuel.KeyValue.Migrator/appsettings.Development.json`
(git-ignored — copy the `.example` next to it):

```jsonc
{
  "ConnectionStrings": {
    "KeyValueSource": "Server=host,1433;Database=immanuel_kv;User Id=...;Password=...;TrustServerCertificate=True"
  },
  "KeyValue": {
    "DataDirectory": "../Immanuel.KeyValue.Web/App_Data",
    "Table": "[immanuel_sa].[KeyVal]"
  }
}
```

> **Use `Server=host,1433`, not `Server=host\INSTANCENAME`.** Resolving a named instance needs
> UDP 1434 (SQL Browser), which is normally blocked over the internet and fails with
> *"error 26 – Error Locating Server/Instance Specified"*.

Then:

```bash
# Always look first - this writes nothing
dotnet run --project src/Immanuel.KeyValue.Migrator -- --dry-run

# Then for real
dotnet run --project src/Immanuel.KeyValue.Migrator
```

Settings layer as `appsettings.json` → `appsettings.{Environment}.json` → environment variables
→ command line, each overriding the last. `KEYVALUE_SOURCE` is a shorthand for
`ConnectionStrings:KeyValueSource` if you would rather pass it per-run. The startup banner
echoes only the server, never the password.
`dotnet run --project src/Immanuel.KeyValue.Migrator -- --help` lists every flag.

If the connection hangs or is dropped immediately, check the host's IP allowlist — shared SQL
hosting usually accepts the TCP connection at the edge and then closes it for addresses that
are not whitelisted, which looks like a hang rather than a clean auth error.

Two things to check in the dry-run output:

- **Mixed-case client keys** are lowercased, because a v2 app key is lowercase and mixed case
  would collide on a case-insensitive file system. Pass `--no-normalize-case` to skip them instead.
- **Client keys that are not 8 characters of `a-z0-9`** cannot become app keys and are listed
  and left behind. v1's `ClientKey` was an unvalidated `varchar(8)`, so a few rows may qualify.
  Their data stays untouched in SQL Server; decide case by case what to do with them.

The tool preserves `CreatedAt` when the v1 table has that column (it shipped as a commented-out
`ALTER`, so some deployments have it and some don't) and detects this automatically.

If a run is interrupted, re-run it — app keys already copied are skipped.

---

## Project layout

```
src/Immanuel.KeyValue.Core/        AppKey validation, schema, SQLite factory, catalog, store
src/Immanuel.KeyValue.Web/         Controllers, host configuration, landing page
src/Immanuel.KeyValue.Migrator/    One-time SQL Server -> SQLite tool
tests/Immanuel.KeyValue.Tests/     Store, legacy-API and v2-API tests
```

`Core` is a plain class library so the web app and the migration tool share one storage
implementation and cannot drift apart on schema.
