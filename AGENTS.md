# Penrill Server: Agent Guide

Use this guide when changing Penrill Server. Follow the Clean Architecture solution template (.NET 10, PostgreSQL, MVC Controllers, no IdentityServer, no Minimal APIs).

## Project boundaries and namespaces

Keep namespaces clean without a `Penrill.` prefix and dependencies pointing inward:

- `src/Sync` (`namespace Sync`): zero-EF, zero-MediatR shared board protocol and pluggable live room engine. Shared with Penrill Cloud via git submodule.
- `src/Domain` (`namespace Domain`): entities, value objects, enums, constants and domain events (`BaseEvent` via `MediatR.Contracts`).
- `src/Application` (`namespace Application`): MediatR CQRS feature folders, validators, authorization, behaviours and interfaces. Depends on `Domain` and `Sync`.
- `src/Infrastructure` (`namespace Infrastructure`): PostgreSQL `ApplicationDbContext` and migrations, ASP.NET Core Identity, asset store, backup writer and `Sync` SPI adapters.
- `src/Web` (`namespace Web`, binary `Penrill.Server`): ASP.NET Core MVC Controllers (`/api/*`, `/web/*`), hosting, CLI and `wwwroot`. Keep controllers thin (`ApiControllerBase` + `Mediator.Send`).
- `tests/Contract` (`namespace ContractTests`): black-box HTTP/WebSocket contract tests with zero dependency on `Domain`, `Application` or `Infrastructure`.
- `src/AppHost` (`namespace AppHost`), `src/ServiceDefaults` (`namespace ServiceDefaults`), `src/Shared` (`namespace Shared`).

## Rules

- The editor API (`docs/API.md`, `docs/PROTOCOL.md`) only grows: never break existing routes, JSON shapes, status codes or `reason` words.
- Use ASP.NET Core MVC Controllers (`[ApiController]`), not Minimal APIs.
- Use ASP.NET Core Identity directly, not Duende IdentityServer.
- MediatR stays at 12.5.0 and AutoMapper is not used (ADR-008).
- After changing the EF model: `dotnet ef migrations add <Name> --project src/Infrastructure --startup-project src/Infrastructure --output-dir Data/Migrations`.
- Writing (docs, comments, commit messages): English, no em dashes.
- Ask before pushing a release tag: it builds `.deb` packages and publishes the APT repository.

## Validation

```bash
dotnet restore Penrill.slnx
dotnet build Penrill.slnx --no-restore --configuration Release
dotnet test Penrill.slnx --no-build --configuration Release
```
