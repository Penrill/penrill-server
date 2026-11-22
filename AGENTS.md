# Penrill Server: Agent Guide

Use this guide when changing either this template repository or a solution generated from it. Prefer existing patterns over introducing new abstractions, and keep changes focused on the requested behaviour.

## Solution context

Penrill Server, generated from the Clean Architecture solution template (`ca-sln`, .NET 10, Web API only) and
reshaped so its core can be shared with Penrill Cloud. Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and
[docs/EXTENDING.md](docs/EXTENDING.md) first.

## Project boundaries

Keep dependencies pointing inward:

- `src/Domain` contains entities, value objects, enums, exceptions, and domain events. It must not depend on Application, Infrastructure, or Web. Its intentional `MediatR.Contracts` dependency is documented in ADR-003.
- `src/Application` contains use cases, validation, authorization metadata, behaviours, models, and interfaces owned by the application. It depends on Domain, never on Infrastructure or Web.
- `src/Infrastructure` implements Application interfaces and contains EF Core persistence, identity, and external-service concerns. It may depend on Application and Shared.
- `src/Web` is the HTTP boundary every host serves under `/api` (a library, not the composition root). Keep endpoints thin: translate HTTP input/output and dispatch Application requests through `ISender`.
- `src/Server` is the self-hosted host and composition root: SQLite and its migrations, backups, the command line, the `/web` pages, certificates and dynamic DNS.
- `tests/Contract` holds the editor API contract every host runs unchanged.
- Domain, Application, Infrastructure, Web and Contract are published as packages for Penrill Cloud. Changing their public surface affects it; see `docs/EXTENDING.md`.
- `src/AppHost` orchestrates the application with Aspire for local development and acceptance tests.
- `src/ServiceDefaults` contains shared observability, health-check, service-discovery, and resilience defaults.
- `src/Shared` contains contracts needed across orchestration boundaries. Do not move domain or application business rules here.

Before changing an architectural boundary, read the accepted records in `docs/decisions`. Add a new ADR from `ADR-000-template.md` when making a significant new architectural decision, and update `docs/decisions/README.md`.

## Application patterns

- Organize use cases by feature, then by command or query, following `src/Application/Boards` and `src/Application/Sharing`.
- Represent requests as MediatR records and keep their handlers in the same feature folder.
- Commands change state; queries read and project state. Keep request-specific validators next to the request.
- Use `IApplicationDbContext` directly in handlers when data access is required. Do not add a generic repository layer merely to hide EF Core; ADR-001 explicitly accepts EF Core abstractions in Application.
- Put business invariants that belong to entities or value objects in Domain. Put use-case orchestration in Application, concrete I/O in Infrastructure, and transport concerns in Web.
- Implement Application-owned interfaces in Infrastructure and register implementations in the relevant `DependencyInjection.cs` file.
- Derive domain events from `BaseEvent`; they are directly publishable through MediatR as described in ADR-003.
- Use the `ca-usecase` item template for new commands and queries when practical. From `src/Application`, for example:

  ```bash
  dotnet new ca-usecase --name CreateThing --feature-name Things --usecase-type command --return-type string
  ```

## Penrill rules

- The editor API (`docs/API.md`, `docs/PROTOCOL.md`) only grows: never rename, remove or change the meaning of a route, field, status code, `reason` word or live frame. Add a scenario to `tests/Contract/ApiContract.cs` for anything editors call.
- A host extends the core through the points in `docs/EXTENDING.md`; it does not change shared behaviour.
- After changing the model, add a migration: `dotnet ef migrations add <Name> --project src/Server --startup-project src/Server --output-dir Data/Migrations`.
- Writing (docs, comments, commit messages): English, no em dashes; rephrase instead.
- Ask before pushing a release tag: it publishes packages, the APT repository and the snap.

## Coding conventions

- Follow `.editorconfig`. C# uses four-space indentation, file-scoped namespaces, sorted `System` directives, and braces.
- Nullable reference types and implicit usings are enabled. All build warnings are treated as errors.
- Keep public names in PascalCase, interfaces prefixed with `I`, parameters and locals in camelCase, and private instance fields in `_camelCase`.
- Prefer the established result, exception, mapping, validation, and endpoint patterns over new framework choices.
- Never commit secrets, connection credentials, generated build artifacts, or untracked machine-specific configuration.

## Tests

Place tests according to the behaviour being changed:

- `tests/Domain.UnitTests`: pure domain behaviour and value objects
- `tests/Application.UnitTests`: isolated Application behaviour, mappings, and pipeline behaviours
- `tests/Application.FunctionalTests`: use cases that exercise persistence and the application stack
- `tests/Infrastructure.IntegrationTests`: concrete infrastructure integrations

Functional tests start the real server with `WebApplicationFactory` on a temporary SQLite folder rather than mocked repositories (see ADR-002).

Run the smallest relevant test project while iterating, then validate the solution before handing off:

```bash
dotnet restore
dotnet build --no-restore --configuration Release
dotnet test --no-build --configuration Release
```

The server's web pages (`src/Server/wwwroot`) are built in the Penrill editor repository (`apps/server`).

## Completion checklist

- The change respects the project dependency boundaries and accepted ADRs.
- New behaviour has tests at the appropriate level.
- The contract tests pass unchanged.
- Formatting, build, and relevant tests pass without introducing warnings.
- Documentation and ADRs are updated when behaviour or architecture changes.
