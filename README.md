# Immanuel.KeyValue

A free, public key-value store you can call over plain HTTP — the service behind
[keyvalue.immanuel.co](https://keyvalue.immanuel.co). Ask for an app key, store values under it,
read them back from anywhere. No SDK, and no account unless you want one.

This repository holds two generations of the same service.

| | [`v1/`](v1/) | [`v2/`](v2/) |
|---|---|---|
| Status | Archived, kept for reference | Active |
| Framework | ASP.NET MVC 5 / Web API 2 (.NET Framework 4.6) | ASP.NET Core (.NET 10) |
| Storage | One shared SQL Server table | One SQLite database per app key, via [Ark.Rapid.Database](https://www.nuget.org/packages/Ark.Rapid.Database) |
| Data access | Inline `SqlCommand` + stored procedures | `Ark.Rapid.Database` |
| Accounts | None | Optional: email + one-time code, a folder per address, a custom API header |
| Front end | Inline-styled page, jQuery, Facebook SDK, Google Analytics | Responsive static page with a live API console, no dependencies |
| Tests | None | 161 |

**v2 keeps every v1 endpoint working, URL for URL.** Code written against this service over the
last decade needs no changes. See [`v2/README.md`](v2/README.md) for what to run and what changed.

## Getting started

Press **F5** and pick *Web (v2)* — it builds, starts the API, and opens the page. Or:

```bash
cd v2
dotnet run --project src/Immanuel.KeyValue.Web
```

Then open <http://localhost:5086>.

## Layout

```
v1/                              The original 2017 application, untouched.
v2/
  src/Immanuel.KeyValue.Core/      Storage: app keys, SQLite files, the catalog.
  src/Immanuel.KeyValue.Web/       HTTP API (v1-compatible + v2 REST) and landing page.
  src/Immanuel.KeyValue.Migrator/  One-time SQL Server -> SQLite migration tool.
  tests/Immanuel.KeyValue.Tests/   Unit and integration tests.
```
