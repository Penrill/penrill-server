# Board sync protocol, version 1

The server is the single source of truth for a shared board. Every board is a room with one writer:
it orders incoming changes, stores them in an append-only log, applies them to the board in memory
and sends each accepted change to everyone in the room. Clients apply their own changes at once,
keep them in a durable outbox until the server accepts them, and replay them on top of whatever the
server sends.

## Board model

A board has a title, a background and a set of items. The server reads only an item's `id`; every
other field is opaque JSON owned by the editor. Images never travel inside items: an image item
carries `asset`, the SHA-256 of the image bytes, uploaded separately.

## Changes

A change is the unit a client creates, retries and the server acknowledges:

```json
{ "id": "6f1c...", "ops": [
  { "op": "put", "item": { "id": "a1", "type": "stroke", "z": 7 } },
  { "op": "patch", "id": "a2", "set": { "x": 10 }, "unset": ["rot"] },
  { "op": "delete", "id": "a3" },
  { "op": "meta", "title": "Physics", "background": "grid" }
] }
```

Applying an operation never fails, so every replica that applies the same changes in the same order
ends with the same board:

| Operation | Effect |
|---|---|
| `put` | creates the item or replaces it whole |
| `patch` | sets and removes fields of an existing item; does nothing when the item is gone |
| `delete` | removes the item if it exists |
| `meta` | sets the title and/or background |

Concurrent edits resolve field by field: the change the server accepted last wins, and a deletion
wins over an edit that arrives after it.

The change `id` is the idempotency key. The server remembers accepted ids for 90 days; a change sent
again is acknowledged with its original sequence number and not applied twice.

Limits: 10 000 operations and 8 MB per change, 1 MB per item, 200 000 items per board.

## Sequence numbers

Each accepted change gets the board's next sequence number (`seq`), starting at 1. A client that
knows `seq` N has applied changes 1..N. The server keeps the bodies of at least the last 1 000
changes; a client further behind receives a snapshot instead.

## Live connection

`GET /api/boards/{id}/live` upgrades to a WebSocket. All frames are UTF-8 JSON text.

1. Client: `{ "type": "hello", "protocol": 1, "token": "...", "after": 42 }`. `after` is the
   last `seq` the client applied, or `null` for none. The token is sent only here, never in the URL.
2. Server: `{ "type": "welcome", "client": "<connection id>", "user": "ann" }`, then either
   `{ "type": "snapshot", "board": { "id", "seq", "title", "background", "items": [...] } }` or
   `{ "type": "changes", "changes": [{ "seq", "author", "change" }] }`, then
   `{ "type": "ready", "seq": 57, "peers": [{ "client", "user" }] }`.
3. After that, in any order:

| Direction | Frame | Meaning |
|---|---|---|
| client to server | `{ "type": "submit", "change": {...} }` | propose a change |
| server to client | `{ "type": "change", "seq", "author", "change" }` | an accepted change, sent to everyone including its author |
| server to client | `{ "type": "ack", "id", "seq" }` | a change the server had already accepted |
| server to client | `{ "type": "reject", "id", "reason" }` | an invalid change; it will never be accepted |
| client to server | `{ "type": "signal", "data": {...} }` | presence (cursor, ink being drawn), relayed and not stored |
| server to client | `{ "type": "signal", "client", "user", "data" }` | a peer's presence |
| server to client | `{ "type": "join" \| "leave", "client", "user" }` | peers entering and leaving |
| client to server | `{ "type": "ping" }` | heartbeat; the server answers `{ "type": "pong" }` |

The server closes the socket with 4401 (sign in again), 4403 (no access), 4404 (no such board),
4400 (protocol error) or 4408 (too slow to keep up). Clients reconnect after any other close.

## HTTP

All routes take `Authorization: Bearer <token>` except register and login.

| Route | Purpose |
|---|---|
| `GET /api/boards` | boards the account can open: `[{ id, title, seq, updated }]` |
| `PUT /api/boards/{id}` | create a board from `{ title, background, items }`; repeating it is safe |
| `GET /api/boards/{id}` | snapshot |
| `GET /api/boards/{id}/changes?after=N` | `{ seq, changes }`, or 410 when the client must take a snapshot |
| `POST /api/boards/{id}/changes` | submit one change, answers `{ seq }` |
| `PUT /api/boards/{id}/assets/{sha256}` | upload image bytes; the server checks the hash |
| `GET /api/boards/{id}/assets/{sha256}` | download image bytes (immutable) |

Errors use status codes: 400 invalid input, 401 sign in, 403 no access, 404 unknown board,
409 identifier taken, 410 snapshot required, 413 too large, 429 slow down.

## Failure model

Connections drop and power goes out at any moment, on the client and on the server.

- A client stores a change in its outbox (durable IndexedDB write) before sending it and deletes it
  only after the server accepted it. A crash at any point either resends the change, which the server
  acknowledges without applying it twice, or loses nothing.
- The server acknowledges a change only after it is committed to disk (SQLite WAL, full sync). A
  change committed but not yet broadcast reaches the author on reconnect, in the catch-up.
- After reconnecting the client sends `after` = its last applied `seq` and replays its outbox on top
  of what the server returns. Operations are idempotent, so replaying a change that is already in a
  snapshot does no harm.
- On start the editor applies the outbox on top of the board saved on the device, so edits made in
  the seconds before a power cut are back even before the server is reachable.
- Clients keep at most 32 changes in flight and send the next one as earlier ones are accepted, so a
  long offline queue never overflows the connection.
- Both sides send heartbeats; a connection silent for 30 seconds is treated as dead and reopened with
  exponential backoff (up to 10 seconds, with jitter).
