# Extending Penrill

Penrill Server and Penrill Cloud share one core: `Penrill.Domain`, `Penrill.Application`, `Penrill.Infrastructure`
and `Penrill.Web`, published as NuGet packages from this repository. A host **extends** the core and never changes
what it does. Editors talk to every host the same way, so a host that changes shared behaviour breaks them.

## The rule

1. Shared behaviour lives in the core and behaves the same on every host.
2. A host adds behaviour only through the extension points below.
3. New routes of a host go under a prefix of its own (Penrill Cloud uses `/api/cloud/...`); existing `/api` routes,
   their JSON and their status codes stay as they are ([API.md](API.md)).
4. Every host runs `Penrill.Contract.Tests` unchanged. A host that does not pass them does not ship.
5. When a host needs a point that does not exist yet, add it to the core first, in its own pull request, with a
   default that keeps today's behaviour. Then the host uses it.

## Extension points

| Point | Default | What a host does with it |
|---|---|---|
| Database provider and migrations | none, the host chooses | `builder.AddInfrastructureServices((sp, options) => options.UseSqlite(...))` or `UseNpgsql(...)`, with `MigrationsAssembly` set to the host's assembly. |
| A context of the host's own | `ApplicationDbContext` | Derive from `ApplicationDbContext`, add `DbSet`s for host-only data and call `AddInfrastructureServices<TContext>`. Shared tables stay as they are. |
| `IAssetStore` | `AddFileAssetStore(directory)` | Register another store, such as blob storage. Content stays addressed by SHA-256. |
| `IBackupWriter` | none | Register one when the host can write a backup itself; without it `AdminService.CanBackup` is false. |
| `IPublicAddress` (Web) | `RequestAddress`, the request's own origin | Replace it to build link pages and certificate pins differently. |
| `AccountOptions`, `LinkOptions`, `RoomOptions` | from configuration | Register an instance before `AddApplicationServices` to set them in code. |
| Use cases of the host's own | none | Put them in a project of the host and pass its assembly: `builder.AddApplicationServices(typeof(SomeHostCommand).Assembly)`. Their handlers, validators and mappings join the same pipeline and behaviours. |
| Pipeline behaviours | the template's five | `services.AddTransient(typeof(IPipelineBehavior<,>), typeof(MyBehaviour<,>))` after `AddApplicationServices`, for cross-cutting work such as an audit log or idempotency keys. |
| Domain events | `CredentialRevokedEvent`, `BoardDeletedEvent`, `BoardAccessRevokedEvent` | Add `INotificationHandler<T>` classes in the host's assembly to react to them. |
| Endpoints | the groups in `Penrill.Web` | Add `IEndpointGroup` classes in the host and map them with `app.MapEndpoints(hostAssembly)` next to `app.MapPenrillApi()`. |

## Adding a use case to the core

Use the template's item template from `src/Application`:

```sh
dotnet new ca-usecase --name CreateThing --feature-name Things --usecase-type command --return-type string
```

Put validation in a validator next to the request, access rules in `[Authorize]`, and refusals in the exceptions of
`Application/Common/Exceptions`. Add an endpoint method to a group in `src/Web/Endpoints`. If editors will call it,
add a scenario to `tests/Contract/ApiContract.cs` and describe the route in [API.md](API.md).

## Running the contract on a host

Reference `Penrill.Contract.Tests` from the host's test project and derive from the fixture with a factory that has
open registration and empty storage for each test:

```csharp
public class CloudApiContractTests : ApiContract<Program>
{
    protected override Task<WebApplicationFactory<Program>> CreateFactoryAsync() => ...;
}
```

`tests/Application.FunctionalTests/ServerApiContractTests.cs` is the server's own.

## Versions

The packages carry the server's version (`Directory.Build.props`) and are published to GitHub Packages on every
`v*` tag by `.github/workflows/packages.yml`. A change that breaks a host's build is a minor version bump while the
version starts with 0, and a major one after 1.0.
