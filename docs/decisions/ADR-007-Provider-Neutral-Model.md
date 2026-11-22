# ADR-007: One EF Core Model, Migrations per Host

## Status

Accepted

## Date

2026-11-22

## Context

The self-hosted server runs on a Raspberry Pi or a school PC and keeps everything in one folder; SQLite suits it.
Penrill Cloud stores paying customers' data and needs point-in-time restore and a managed database; PostgreSQL suits it.
Both run the same use cases on the same model.

## Decision

`ApplicationDbContext` and its entity configurations live in `Penrill.Infrastructure` and do not name a provider.
Each host picks the provider and keeps its own migrations in its own assembly (`MigrationsAssembly`). Where a provider
needs it, the context adjusts conventions: on SQLite `DateTimeOffset` is stored as UTC ticks so it can be compared and
ordered in SQL. Use cases use only what both providers translate: LINQ, `ExecuteUpdate`, `ExecuteDelete` and
transactions; no raw SQL.

## Rationale

- Separate models per host would duplicate every configuration and let them drift.
- One provider for both would cost the server its single-folder install or the cloud its managed database.

## Consequences

**Easier:**
- A model change is made once; each host adds a migration for it, and CI checks that the migrations match the model.

**Harder:**
- Use cases cannot use provider-specific SQL. When one is needed, it goes behind an interface the host implements.
