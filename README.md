# Penrill Server

Keeps Penrill boards in sync across your devices and lets you share them, on your own hardware.
Licensed under FSL-1.1-MIT. Design: `docs/CONNECT.md` in the editor repository; wire protocol: `docs/PROTOCOL.md`.

## Install

On Debian, Ubuntu or Raspberry Pi OS (64-bit, amd64 or arm64):

```sh
sudo apt install ./penrill-server_0.3.0_arm64.deb
```

The package starts the service and prints a setup link with a QR code. Open it in a browser on any
device in the same network, create the owner account, and you are done. Lost the link? Run
`sudo penrill-server setup`.

The browser warns about the certificate once: the server makes its own. Its fingerprint is shown on
the setup page, in Admin › Server and by `sudo penrill-server status`. Penrill itself checks it for you.

## Connect Penrill

In Penrill, open Sync › Your server and enter the address the server's home page shows. Servers on
the same network are listed there by themselves. Penrill then asks you to allow it in the browser,
and boards start syncing. No password is typed into Penrill. On a tablet, scan the QR code on the
server's home page: penrill.com/connect offers the app or Penrill in the browser. The server's
address stays in the link's fragment, so it never reaches penrill.com.

## Add people

Registration is by invite only. Create an invite link in Admin › People, or:

```sh
sudo penrill-server invite            # one person, valid 7 days
sudo penrill-server invite --uses 25  # a class
```

Forgotten passwords: Admin › People › Reset password, or `sudo penrill-server reset-password NAME`,
gives a one-hour link. Boards from server versions before accounts can be given an owner in Admin › Boards.

## Command line

```
penrill-server status            address, certificate, users, boards
penrill-server setup             new setup link while there is no owner
penrill-server link              address, connect link and QR code for Penrill
penrill-server invite            invite link (--admin, --uses N, --days N)
penrill-server reset-password U  password reset link
penrill-server users | devices   list (devices --revoke ID)
penrill-server disable | enable U
penrill-server rename NAME
penrill-server backup FILE       all boards, images and accounts in one zip
```

The commands talk to the running service over `/run/penrill-server/admin.sock`, so run them with sudo.

## Service

Boards live in `/var/lib/penrill-server` and survive upgrades. Logs: `journalctl -u penrill-server`.
The service listens on port 5081 with TLS on every interface and is announced on the network as
`_penrill._tcp` through Avahi. For a web editor on another origin, set `PENRILL_EDITOR_ORIGINS`
(comma separated) in a systemd override.

## Development

```sh
dotnet run --project src/Penrill.Server   # http://127.0.0.1:5081, data in src/Penrill.Server/data
dotnet test
```

The server's web pages (setup, sign-in, devices, administration) are a React app that lives in the
Penrill repository under `apps/server` and talks to the JSON API in `src/Penrill.Server/Web`. Its
build is committed in `src/Penrill.Server/wwwroot`, so the server builds and ships without Node. To
change the pages, check out Penrill next to this repository and run `npm run dev:server-ui` there
(http://localhost:5175, with this server running) and `npm run build:server-ui` to update `wwwroot`.

`PENRILL_CONNECT_PAGE` sends connect links to a local web editor (`http://localhost:5174/app/`)
instead of penrill.com/connect. `PENRILL_ADDRESS_MODE=lan` switches to the installed behaviour (TLS on all interfaces, port from
`PENRILL_PORT`). `PENRILL_OPEN_REGISTRATION=true` allows anyone to register, as Penrill Cloud does.

Packages: `sh packaging/build-deb.sh [VERSION]` on Debian or Ubuntu with the .NET 10 SDK and dpkg-dev
builds amd64 and arm64 into dist/.
