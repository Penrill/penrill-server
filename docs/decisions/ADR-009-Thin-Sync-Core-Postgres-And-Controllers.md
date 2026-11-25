# ADR-009: Thin Shared Sync Library, PostgreSQL Everywhere, and MVC Controllers

## Status

Accepted

## Date

2026-11-25

## Context

Shipping `Domain`, `Application`, `Infrastructure`, `Web` and `Contract.Tests` as five NuGet packages forced every host to share concrete sealed services, compile-time limits, a SQLite-constrained EF model and a username-as-primary-key identity design. It also forced a public `v*` server release whenever Penrill Cloud needed a core hook.

## Decision

1. **One thin shared library (`src/Sync`, namespace `Sync`)** holds only the board document model, change codec, runtime `BoardLimitPolicy`, `BoardPermission`, `ServerCapabilities`, and the pluggable live room engine (`BoardRoom`, `RoomRegistry`, `LiveSessions`, `LiveConnections`) behind explicit SPI interfaces (`IBoardStore`, `IBoardHistorySink`, `ILiveAuthenticator`, `ILiveAccessPolicy`, `ILiveEventObserver`, `IRoomBackplane`).
2. **Decoupled black-box contract tests (`tests/Contract`, namespace `ContractTests`)** have zero dependency on `Domain`, `Application` or `Infrastructure`.
3. **Git submodule consumption:** downstream hosts such as Penrill Cloud consume `src/Sync` and `tests/Contract` via a pinned git submodule and direct `<ProjectReference>`, eliminating GitHub Packages auth tokens and `v*` release coupling.
4. **PostgreSQL everywhere:** Penrill Server runs on PostgreSQL via `Npgsql.EntityFrameworkCore.PostgreSQL` and Aspire (`AddPostgres`), dropping SQLite and its `DateTimeOffset` conversion workarounds.
5. **Clean Architecture with MVC Controllers and clean namespaces:** `src/Web` is the single web/CLI host (merging the former `src/Web` library and `src/Server` executable, keeping binary name `Penrill.Server`), using ASP.NET Core MVC Controllers (`ApiControllerBase`) instead of Minimal APIs, ASP.NET Core Identity without IdentityServer, and clean root namespaces (`Domain`, `Application`, `Infrastructure`, `Web`, `Sync`, `Shared`, `ServiceDefaults`, `AppHost`).

## Consequences

**Easier:**
- Penrill Server stays lean and focused on self-hosted usage on PostgreSQL.
- Penrill Cloud owns its own SaaS identity, workspaces, billing, quotas, storage, version history and controllers while reusing the exact live board sync engine and editor wire contract.
- Local development and CI build directly from source with no package publish step.

**Harder:**
- Self-hosted installations require a PostgreSQL database (configured via `ConnectionStrings__penrill` or local Aspire/Docker).
