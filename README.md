# Penrill Server

Local ASP.NET Core API for synchronized Penrill boards, licensed under FSL-1.1-MIT.
Run `dotnet run --project src/Penrill.Server` using .NET 10. The development API binds to http://127.0.0.1:5081. SQLite data lives under the server project in data/boards.db. Override with PENRILL_DATA_DIR.

GET /api/boards lists board IDs, titles, revisions and update times. GET /api/boards/{id}?revision=N reads that revision if it is still current. PUT /api/boards/{id}?title=TITLE&revision=N writes an exported UTF-8 board as application/json. Use revision zero and a new 32-character hex ID to create a board. Updates require the current revision. A stale write returns HTTP 409. Repeating a write of identical title and bytes after a lost response is safe. Imported boards from versions without accounts stay in the database without an assigned owner. A server administrator can assign an existing board to a registered account locally (see below).

Configure PENRILL_EDITOR_ORIGINS with comma-separated browser origins. Run `dotnet build src/Penrill.Server` and `node --test tests/sync.mjs` to test writes, conflicts and persistence across restarts. See the editor README for the three-repository Aspire setup.

## Raspberry Pi OS (64-bit)

Use the arm64 Debian package on Raspberry Pi OS Bookworm or Trixie (64-bit).
The package includes the .NET runtime. Install and start the service on the Pi:

```sh
sudo apt install ./penrill-server_0.2.0_arm64.deb
sudo systemctl daemon-reload
sudo systemctl enable --now penrill-server
curl --fail http://127.0.0.1:5081/health
```

The service stores boards in /var/lib/penrill-server. Package upgrades preserve them.
After an upgrade run `sudo systemctl restart penrill-server`.
Inspect logs with `journalctl -u penrill-server`.

For a private connection from your computer, keep the service on loopback and run
`ssh -N -L 5083:127.0.0.1:5081 USER@raspberrypi.local` on the computer.
In Settings, select Self-hosted and connect to http://127.0.0.1:5083.
Keep the SSH session running while syncing. Create an account or sign in on this server.
For a web editor on a different origin, set PENRILL_EDITOR_ORIGINS in a systemd
service override and restart the service.

To build on Debian or Ubuntu with the .NET 10 SDK and dpkg-dev, run
`sh packaging/build-deb.sh`. The package is written to dist/.

## Accounts and sharing

Each installation owns its accounts, sessions, shared folders and template marketplace.
Register or sign in through the editor. Usernames are case-insensitive. Sessions expire after 30 days.
Boards belong to their creators. Folder owners add existing users by username. Members can
edit boards shared in that folder. A board can only be shared by its owner.
Templates published to a folder are visible to members. Marketplace templates are visible
to signed-in users of the same installation.

Accounts use salted password hashes. Requests use a bearer session token over HTTPS or a local tunnel.
Registration is open to clients with network access. Keep a private deployment behind the tunnel or VPN.

To restore ownership of pre-account boards, back up the database first. Use the local SQLite
shell to list unassigned boards and registered accounts, then assign the intended board explicitly:

```sql
SELECT id,title FROM boards WHERE id NOT IN (SELECT board FROM owners);
SELECT name FROM accounts;
INSERT INTO owners(board,user)
SELECT boards.id,accounts.name FROM boards,accounts WHERE boards.id='BOARD_ID' AND accounts.name='USERNAME';
```
