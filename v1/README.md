# Immanuel.KeyValue v1 (archived)

The original 2017 service, kept for reference. **It is superseded by [`../v2`](../v2), which
serves every endpoint below at the same URL with the same response format.**

Nothing here is maintained. Don't deploy it.

## What it was

ASP.NET MVC 5 + Web API 2 on .NET Framework 4.6, backed by a single SQL Server table
(`[immanuel_sa].[KeyVal]`) with three stored procedures. See [`table_kv.sql`](table_kv.sql).

| File | |
|---|---|
| `Immanuel.KeyValue/Controllers/KeyValController.cs` | The whole API |
| `Immanuel.KeyValue/Views/Home/Index.cshtml` | Landing page and try-it console |
| `Immanuel.KeyValue/Hubs/ChatHub.cs`, `Views/Home/Chat.cshtml` | Unused SignalR sample scaffolding — not carried into v2 |
| `table_kv.sql` | Table and stored procedures |
| `packages/` | Committed NuGet packages, from before restore-on-build |

## API

`GetAppKey`, `GetValue`, `UpdateValue`, `ActOnValue`, `GetCount`, `GetIp` — all under
`/api/KeyVal/`. v2 implements all six identically; see [`../v2/README.md`](../v2/README.md).

## Known issues (fixed in v2)

- Every user shared one table, so all traffic contended on the same locks.
- Any 8-character string became a valid app key on first write; there was no registration.
- App keys came from a shared `System.Random`, making issued keys predictable.
- `UpdateValue` always returned `true`, even when the write did nothing.
- Incrementing a key that didn't exist silently did nothing.
- No way to list or delete keys.
- No rate limiting on a public, unauthenticated, free service.
- The connection string lived in source (see commit `86ab543`).
- No tests.

## Migrating the data

`../v2/src/Immanuel.KeyValue.Migrator` copies this table into v2's per-app-key SQLite files.
Run it with `--dry-run` first. See [`../v2/README.md`](../v2/README.md#migrating-from-v1).
