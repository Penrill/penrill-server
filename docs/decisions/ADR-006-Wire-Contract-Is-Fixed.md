# ADR-006: The Editor API Does Not Change Shape

## Status

Accepted

## Date

2026-11-22

## Context

Desktop editors update on their own schedule, so a server or cloud release meets editors of many versions. The
routes, JSON, status codes and the live protocol are what keeps them working.

## Decision

The routes under `/api`, their JSON, their status codes, the `reason` words and the live protocol in
[PROTOCOL.md](../PROTOCOL.md) are the contract ([API.md](../API.md)). They change only by adding: a new route, a new
optional field, a new frame type older editors ignore. Errors use problem details, which editors accept because they
read only the status and `reason`. `Penrill.Contract.Tests` checks the contract on every host.

## Rationale

The alternative, versioned routes (`/api/v2`), would leave every host serving two versions for as long as old editors
exist. Adding without removing keeps one version.

## Consequences

**Easier:**
- Any editor works with any host.

**Harder:**
- A field cannot be renamed or dropped; it can only be joined by a new one.
