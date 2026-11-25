# Sharing Sync and the Editor Contract

Penrill Server and Penrill Cloud share only two projects from this repository (consumed via a pinned git submodule,
see [ADR-009](decisions/ADR-009-Thin-Sync-Core-Postgres-And-Controllers.md)):

1. `src/Sync` (`namespace Sync`): board document model, strict change codec, runtime `BoardLimitPolicy`,
   `BoardPermission`, `ServerCapabilities`, and the live WebSocket room engine (`BoardRoom`, `RoomRegistry`,
   `LiveSessions`, `LiveConnections`).
2. `tests/Contract` (`namespace ContractTests`): black-box HTTP and WebSocket editor API contract fixtures.

## Implementing a Host on `Sync`

Register the engine with `services.AddBoardSync()` and supply three host-specific SPI implementations:

| Interface | Required | Responsibility |
|---|---|---|
| `IBoardStore` | Yes | Loads board snapshots and recent changes, appends sequenced changes atomically, compacts snapshots. |
| `ILiveAuthenticator` | Yes | Validates the WebSocket hello token and returns `LiveIdentity(UserId, DisplayName, CredentialId, ExpiresAt, IsGuest)`. |
| `ILiveAccessPolicy` | Yes | Evaluates `BoardPermission` (`None`, `View`, `Comment`, `Edit`, `Owner`) and runtime `BoardLimitPolicy` (including `MaxItems`, `MaxChangeBytes`, `MaxConcurrentPeers`). |
| `IBoardHistorySink` | Optional | Receives committed snapshots for version history archival. |
| `ILiveEventObserver` | Optional | Receives live connect, disconnect, change accepted and change rejected events for auditing or metering. |
| `IRoomBackplane` | Optional | Publishes frames across nodes (defaults to in-process). |

## Running the Editor Contract Tests

Reference `tests/Contract/Contract.csproj` and derive from `ApiContract<TProgram>`, implementing
`CreateFactoryAsync`, `SeedBoardAssetBytesAsync` and `CreateAdminInviteTokenAsync`.
