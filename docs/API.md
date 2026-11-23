# The editor API

What Penrill editors (desktop, web and the cloud workspace) call. Every host serves it unchanged; the contract tests in
`tests/Contract` check it. The live protocol on `/api/boards/{id}/live` is in [PROTOCOL.md](PROTOCOL.md).

## Conventions

- JSON in camelCase. Times are ISO 8601 strings, except template versions (`updated`), which are Unix milliseconds.
- Signed-in requests send `Authorization: Bearer <token>`: 64 hex characters from `/api/login`, `/api/register`,
  `/api/link/poll` or `/api/join`. Without a valid one a protected route answers 401.
- Board ids are 32 or 64 lower-case hex characters; image hashes are the lower-case hex SHA-256 of the bytes.
- Errors are RFC 9110 problem details. When the refusal has a reason editors act on, it is in `reason`:
  `invalid` (a change that does not parse), `hash-mismatch` (image bytes and hash differ), `board-full` (over 200,000 items).
- Sign-in, registration, link start and join allow 20 requests a minute per address; link polls 240. Over that: 429.
- Limits: a board 32 MiB, a change 8 MiB, an image 25 MiB, all images on a board 256 MiB together (413 beyond).

## Accounts

| Route | Signed in | Answer |
|---|---|---|
| `GET /api/server` | no | `{service: "penrill", name, registration: "open" \| "invite", link: true, pin}`; `pin` is null unless the certificate is not publicly trusted |
| `POST /api/register` `{name, password}` | no | `{name, token}`; 403 when registration is closed, 409 when the name is taken, 400 when invalid |
| `POST /api/login` `{name, password}` | no | `{name, token}`; 401 when refused |
| `POST /api/logout` | yes | 204; this credential's live connections close |

Names are 3 to 32 of `a-z 0-9 _ -` (sign-in lowercases them); passwords are 10 to 256 characters.

## Device linking

| Route | Signed in | Answer |
|---|---|---|
| `POST /api/link/start` `{device, app}` | no | `{deviceCode, userCode, verifyUrl, verifyUrlComplete, expiresIn}`; 503 when too many requests wait |
| `POST /api/link/poll` `{deviceCode}` | no | waits up to 15 s; 200 `{name, token}`, 202 pending, 403 denied, 410 expired or used |
| `GET /api/link/{code}` | yes | `{code, device, app, address, created}` of a waiting request; 404 otherwise |
| `POST /api/link/{code}` `{allow}` | yes | 204; 404 when nothing waits under the code |
| `GET /api/devices` | yes | `[{id, name, created, lastUsed, current}]` |
| `POST /api/devices/{id}/revoke` | yes | 204; 404 |

## Boards

| Route | Answer |
|---|---|
| `GET /api/boards` | `[{id, title, seq, updated, owner, copying, shared}]`, the boards the user can open, newest first. `copying`: the user may duplicate, export or copy the board out (always for the owner). `shared`: for the owner, someone else can open it |
| `PUT /api/boards/{id}` `{title, background, items}` | 201 `{seq: 0, created: true}`; 200 `{seq, created: false}` when it exists and the user can open it; 409 when it is someone else's; 403 for guests; 415 unless JSON |
| `GET /api/boards/{id}` | `{id, seq, title, background, items}` |
| `GET /api/boards/{id}/changes?after=N` | `{seq, changes: [{seq, author, change}]}`; 410 when the changes are no longer kept |
| `POST /api/boards/{id}/changes` `{id, ops}` | `{seq}`, also for a change sent again; 400 `{reason}` when refused; 503 when it could not be stored |
| `DELETE /api/boards/{id}?copies=keep\|remove` | 204; only the owner (403), 404 when missing. `copies=remove` takes it off the devices of the people it was shared with; by default they keep it |
| `PUT /api/boards/{id}/assets/{hash}` | 204; image types only (415) |
| `GET /api/boards/{id}/assets/{hash}` | the image, cached privately for a year |
| `GET /api/boards/{id}/live` | WebSocket, see [PROTOCOL.md](PROTOCOL.md) |

Opening a board someone else owns, or one the user cannot open, answers 403; a board that does not exist, 404, with `reason: removed` when its owner deleted it with `copies=remove`.

## Sharing

| Route | Answer |
|---|---|
| `GET /api/community` | `{folders: [{id, name, owner}], members: [{folder, name}], boards: [{folder, id, title, seq, updated}], templates: [{id, name, folder, author, updated}]}` |
| `POST /api/folders` `{name}` | 204 |
| `POST /api/members` `{folder, name}` | 204; 404 unless the folder is the caller's and the account exists |
| `POST /api/share-board` `{folder, board}` | 204; 409 unless the caller owns the board and is in the folder |
| `GET /api/boards/{id}/people` | `["name", ...]`; owner only, 404 for others |
| `POST /api/boards/{id}/people` `{name}` | 204; 404 when the board is not the caller's or the account does not exist |
| `DELETE /api/boards/{id}/people/{name}` | 204; they are disconnected |
| `GET /api/boards/{id}/link` | `{token}`, null while the draw link is off; owner only |
| `POST /api/boards/{id}/link` | `{token}`; the same token while it stays on |
| `DELETE /api/boards/{id}/link` | 204; guests who came in by it lose the board and are disconnected |
| `PUT /api/boards/{id}/copying` `{allowed}` | 204; whether the people it is shared with may duplicate, export and copy it out. Off for new boards; owner only (403) |
| `POST /api/join` `{token, name}` | `{name, token, board}`; a new guest account, or `token: null` when the caller is already signed in |

Guests (accounts made by a draw link) cannot create boards, folders, shares or templates: 403.

## Templates

| Route | Answer |
|---|---|
| `POST /api/templates?name=&folder=` body: a board file | `{id, name, folder, author, updated}`; 404 when the folder is not the caller's |
| `PUT /api/templates/{id}?name=` body: a board file | the same; the author only |
| `DELETE /api/templates/{id}` | 204; the author, an admin or the owner |
| `GET /api/templates/{id}` | the board file |

A board file is JSON with `format: "inkboard"`, a `version` of at least 1 and an `items` array.
