# ADR-005: Opaque Bearer Tokens Stored as Hashes, Next to ASP.NET Core Identity

## Status

Accepted

## Date

2026-11-22

## Context

The template signs in with ASP.NET Core Identity's bearer tokens (`MapIdentityApi`). Penrill editors already send
`Authorization: Bearer <64 hex characters>` and keep the token for months on a device. They also need to see and
revoke their devices, and open live connections must close the moment a token stops being valid.

## Decision

Accounts, passwords and roles are ASP.NET Core Identity (`ApplicationUser`, `UserManager`, roles). Tokens are not
Identity tokens: they are 32 random bytes in hex, stored only as SHA-256 in `Credential` with a kind (session or
device), a device name and an optional expiry. `BearerSessionHandler` looks them up on each request.

## Rationale

- Identity's protected bearer tokens cannot be listed or revoked one by one without extra storage, and their format
  would change what editors send.
- JWTs would need a revocation list for the same reason, and are larger for no gain on a single host.
- Hashing means a copy of the database does not let anyone sign in.

## Consequences

**Easier:**
- Devices can be listed and revoked; signing out closes live connections at once (`CredentialRevokedEvent`).
- Editors keep their tokens and their code unchanged.

**Harder:**
- Each signed-in request reads the credential and the account. Both are indexed lookups.
