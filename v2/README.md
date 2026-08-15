# Immanuel.KeyValue v2

The key-value service rebuilt on ASP.NET Core 10, storing each app key in its own SQLite
database through [Ark.Rapid.Database](https://www.nuget.org/packages/Ark.Rapid.Database).

Every v1 endpoint still works at the same URL with the same response format. Nothing written
against the old service needs to change.

Sign-up is optional and additive: an account gets you a folder of your own, app keys you cannot
lose, and a custom HTTP header that authenticates your calls. Anonymous app keys behave exactly
as they always have.

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
dotnet test                                        # 161 tests
dotnet build                                       # whole solution
```

Databases are created under `src/Immanuel.KeyValue.Web/App_Data/`.

### Local settings

Each project has an `appsettings.Development.json.example`. Copy it to
`appsettings.Development.json` and edit — **that file is git-ignored**, which is where anything
resembling a credential belongs: the migrator's connection string, and the web app's SMTP
password.

```bash
cp src/Immanuel.KeyValue.Migrator/appsettings.Development.json.example \
   src/Immanuel.KeyValue.Migrator/appsettings.Development.json
```

The web app runs fine without one. With no SMTP configured it accepts `Auth:MasterOtp` for every
sign-in, and the development settings set `Auth:RevealMasterOtp` so the console fills the code in
for you — so signing up locally needs no mailbox at all.

---

## How storage works

```
App_Data/
  _catalog.db                     Registry of issued app keys + a cached key count for each.
  _users.db                       Accounts, one-time codes, sessions.
  3cg7aby9.db                     An anonymous app key. The file name is the app key.
  pk4m2xn8.db
  raj_at_immanuel.co/             One folder per signed-up address: "@" becomes "_at_".
    7hq2mz4v.db                   App keys issued to that account.
    b9wk3ct1.db
```

v1 kept every user's rows in one SQL Server table, so all traffic contended on one set of
locks. In v2 an app key's reads and writes touch only that app key's file, and every file is in
WAL mode so readers never block on a writer.

The two shared files are deliberately kept out of the hot path. `_catalog.db` is never touched by
a read or an overwrite, and `LastAccessAt` is stamped at most once per app key every five
minutes; only issuing a key, or adding/removing one, writes to it. `_users.db` is touched on
sign-in and when a request carries a credential, never otherwise.

**Both the app key and the folder name are file-system paths, so both are validated as security
boundaries.** An app key is exactly eight characters of `a-z0-9`; a folder name has to map back
to an email address through a narrow character set. Both are checked before anything reaches the
file system, which rules out `../`, absolute paths, and the underscore-prefixed names the shared
files use. See `AppKey.cs`, `UserFolder.cs` and their tests.

App keys stay globally unique whichever folder they live in, so a caller never has to say which
account a key belongs to — the store resolves the folder once and caches it.

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

### Accounts

| Method | Path | | Credential |
|---|---|---|---|
| `GET` | `/api/v2/auth/state` | Are accounts on, and how are codes delivered | — |
| `POST` | `/api/v2/auth/signup` | Register an address and send it a code | — |
| `POST` | `/api/v2/auth/signin` | Send a code to an existing account | — |
| `POST` | `/api/v2/auth/verify` | Exchange the code for a session token | — |
| `POST` | `/api/v2/auth/signout` | End the session | Bearer |
| `GET` | `/api/v2/me` | Account, app keys and API header | Bearer |
| `GET` `POST` | `/api/v2/me/appkeys` | List, or issue into your folder | Bearer |
| `DELETE` | `/api/v2/me/appkeys/{appkey}` | Delete a key and everything under it | Bearer |
| `GET` `PUT` `DELETE` | `/api/v2/me/header` | Read, set or remove the custom header | Bearer |

---

## Accounts

Optional, and off the hot path. Everything above works without one.

### Signing in

There are no passwords. You give an address, the service sends a six-digit code, and you send the
code back for a session token — which the console keeps and puts in `Authorization: Bearer`.

```bash
curl -X POST https://keyvalue.immanuel.co/api/v2/auth/signup \
  -H 'Content-Type: application/json' -d '{"email":"you@example.com"}'

curl -X POST https://keyvalue.immanuel.co/api/v2/auth/verify \
  -H 'Content-Type: application/json' -d '{"email":"you@example.com","code":"123456"}'
# {"token":"...","expiresAt":"...","account":{...}}
```

Codes are hashed before they are stored, expire after ten minutes, and are thrown away after five
wrong guesses. Session tokens are hashed the same way, so a copy of `_users.db` is not a set of
working credentials.

**With no SMTP relay configured, the code from `Auth:MasterOtp` is what gets accepted** — for
every address. That is what makes a fresh checkout usable with no mail setup, and it means anyone
who knows it can sign in as anybody. Configure `Auth:Smtp` before putting the service anywhere
public.

### Your folder

Signing up creates `App_Data/<your address with @ replaced by _at_>/`. App keys you issue while
signed in are created there, are listed when you sign in, and are deleted with their data when
you ask. That is the difference an account makes: an anonymous app key that you lose is gone,
because nothing maps back to it.

### The custom API header

An account chooses its own HTTP header — both the name and the value:

```bash
curl -X PUT https://keyvalue.immanuel.co/api/v2/me/header \
  -H 'Authorization: Bearer <session token>' -H 'Content-Type: application/json' \
  -d '{"name":"x-yourapp-token","value":"kv_something_nobody_can_guess"}'
```

From then on, any API call carrying that header is treated as yours, and calls to your app keys
are refused without it:

```bash
curl https://keyvalue.immanuel.co/api/v2/appkeys/7hq2mz4v/keys \
  -H 'x-yourapp-token: kv_something_nobody_can_guess'
```

Names are matched case-insensitively and stored lowercase, must be an HTTP token of up to 64
characters, and cannot be one the server or a proxy already gives a meaning (`Authorization`,
`Host`, `X-Forwarded-For` and the rest of `Auth:ReservedHeaderNames`). Values are 8–128 printable
ASCII characters. The pair is unique across accounts, so a call can never be ambiguous about who
made it.

Three things worth knowing:

- **The header does not manage the account.** `/api/v2/me` needs the session token instead, so a
  leaked header cannot mint more app keys or rewrite the credential that leaked.
- **The value is stored as given, and shown back to you**, because the console fills it into
  every request for you. It is a bearer credential in a page you are signed in to — treat it like
  a password, and rotate it by saving a new one.
- **Anonymous app keys are unaffected.** They have no owner, so they stay open to whoever holds
  the key, which is what keeps a decade of v1 callers working.

### The console

The landing page is the documentation and the test client. Signed in, it fills your header, your
session token and your app keys into a form that will call any endpoint on the list against the
live service, and shows the equivalent `curl` next to the response.

---

## Behaviour changes

Four in the store itself, all deliberate:

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

Accounts add a fifth, which touches no existing caller: **an app key issued to an account is a
403 without that account's API header**, on both APIs. Anonymous app keys — every key v1 ever
handed out — have no owner and stay open to whoever holds the key.

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
  "Auth": {
    "Enabled": true,                   // false closes /api/v2/auth and /api/v2/me only
    "MasterOtp": "000000",             // Accepted for every address while Smtp.Host is empty
    "RevealMasterOtp": false,          // true returns it in the response - development only
    "OtpLifetimeMinutes": 10,
    "OtpMaxAttempts": 5,               // Wrong guesses before the code is discarded
    "SessionLifetimeHours": 336,
    "MaxAppKeysPerUser": 10,
    "Smtp": {
      "Host": "",                      // Empty means no delivery, so MasterOtp is used
      "Port": 587,
      "UseSsl": true,                  // STARTTLS
      "UserName": "",
      "Password": "",                  // Belongs in appsettings.Development.json, not here
      "FromAddress": "",
      "FromName": "Immanuel KeyValue"
    }
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

A third applies once accounts are in use: **configure `Auth:Smtp`, or turn `Auth:Enabled` off.**
Without a relay every sign-in accepts `Auth:MasterOtp`, which is one shared secret standing
between the internet and every account. `Auth:RevealMasterOtp` must stay false in production for
the same reason — it puts that secret in an unauthenticated response.

CORS stays wide open, which is safe here because the credential is a header a caller has to opt
into sending rather than a cookie a browser attaches on its own: there is no cross-site request
to forge.

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
src/Immanuel.KeyValue.Core/        AppKey and folder validation, schema, SQLite factory,
                                   catalog, store, accounts and one-time codes
src/Immanuel.KeyValue.Web/         Controllers, caller resolution, host configuration,
                                   landing page and API console
src/Immanuel.KeyValue.Migrator/    One-time SQL Server -> SQLite tool
tests/Immanuel.KeyValue.Tests/     Store, account, legacy-API and v2-API tests
```

`Core` is a plain class library so the web app and the migration tool share one storage
implementation and cannot drift apart on schema.

Where the account pieces live:

| | |
|---|---|
| `Core/UserFolder.cs` | Email ⇄ folder name, and the validation that makes it a safe path |
| `Core/AccountService.cs` | Sign-up, sign-in, codes, sessions, the custom header |
| `Core/UserDirectory.cs` | Everything in `_users.db`, data access only |
| `Core/OtpSender.cs` | SMTP delivery, and the `CanSend` flag that selects the master code |
| `Web/Auth/CallerMiddleware.cs` | Turns the two credentials into a caller, once per request |
| `Web/Auth/AppKeyAccess.cs` | The single rule both controllers use to authorise an app key |
