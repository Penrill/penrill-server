# ADR-004: One Core for Penrill Server and Penrill Cloud, Shipped as Packages

## Status

Superseded by [ADR-009](ADR-009-Thin-Sync-Core-Postgres-And-Controllers.md)

## Date

2026-11-22

## Context

Penrill Cloud serves the same API to the same editors as the self-hosted server. Until now it compiled the server's
projects from a sibling checkout. The cloud is going to grow paid features (plans, quotas, an audit log), and those must
not change how boards, sharing or sign-in behave for editors.

## Decision

Domain, Application, Infrastructure, Web and the contract tests are packages built from this repository
(`Penrill.Domain`, `Penrill.Application`, `Penrill.Infrastructure`, `Penrill.Web`, `Penrill.Contract.Tests`) and
published to GitHub Packages on every release tag. Each host (the server here, Penrill Cloud in its own repository)
composes them, adds its own database provider and migrations, and extends them only through the points listed in
[EXTENDING.md](../EXTENDING.md). Every host runs the contract tests.

## Rationale

- **Cloud as a fork of the server** would let the two drift: a fix in one is easily forgotten in the other, and editors
  would meet two slightly different APIs.
- **Only Domain and Application as packages**, each host with its own Infrastructure and Web, still duplicates the
  endpoints, the bearer scheme and the EF model, which is where wire compatibility actually lives.
- **One repository for both hosts** would put the closed cloud deployment next to the source-available server.

## Consequences

**Easier:**
- A fix or a new use case lands once and reaches both hosts.
- The contract tests make "the cloud only extends" checkable.

**Harder:**
- The cloud moves to a new core version deliberately, by updating its package version.
- Changing something a host overrides needs a package release first.
