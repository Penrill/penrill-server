# Penrill Server

Self-hosted board synchronization and sharing server for [Penrill](https://penrill.com). Keeps your boards in sync across devices and lets you collaborate with your team or classroom on your own hardware (such as a Raspberry Pi or home/school server).

- **License:** [FSL-1.1-MIT](LICENSE)
- **Specs:** `docs/CONNECT.md` (in the editor repository) · [Editor API](docs/API.md) · [Wire Protocol](docs/PROTOCOL.md)
- **Architecture:** [Overview](docs/ARCHITECTURE.md) · [Extending](docs/EXTENDING.md) · [Decisions](docs/decisions/README.md)

---

## Quick Start

Penrill Server runs on **64-bit Linux** (`arm64` for Raspberry Pi 3/4/5 and `amd64` for x86_64 servers/PCs), including **Raspberry Pi OS (64-bit)**, **Debian**, and **Ubuntu**.

### Option 1: Install via APT (Recommended for Raspberry Pi OS, Debian & Ubuntu)

Install and configure the official APT repository in one step:

```sh
curl -fsSL https://penrill.github.io/penrill-server/install.sh | sudo sh
```

Or add the APT repository manually:

```sh
sudo mkdir -p /usr/share/keyrings
curl -fsSL https://penrill.github.io/penrill-server/penrill-archive-keyring.gpg \
  | sudo tee /usr/share/keyrings/penrill-archive-keyring.gpg >/dev/null
echo "deb [signed-by=/usr/share/keyrings/penrill-archive-keyring.gpg] https://penrill.github.io/penrill-server stable main" \
  | sudo tee /etc/apt/sources.list.d/penrill.list
sudo apt update
sudo apt install penrill-server
```

Future updates are installed automatically alongside your system packages via `sudo apt update && sudo apt upgrade`.

### Option 2: Install a `.deb` Package Manually

Download the `.deb` package for your architecture (`arm64` for Raspberry Pi, `amd64` for x86_64) from [GitHub Releases](https://github.com/Penrill/penrill-server/releases/latest) and install it with `apt`:

```sh
# Raspberry Pi OS / ARM64
sudo apt install ./penrill-server_0.3.0_arm64.deb

# Debian / Ubuntu x86_64
sudo apt install ./penrill-server_0.3.0_amd64.deb
```

---

## First-Time Setup & Interactive Configurator

When installed via `install.sh`, the **Penrill Server Interactive Configurator** launches automatically in your terminal. You can also launch it anytime by running:

```sh
sudo penrill-server
```

### Option A: Terminal Setup Wizard

Choose **Configure right here in the terminal** to set up your server directly over SSH without needing a browser:
1. Enter a **Server display name** (e.g. `Penrill on raspberrypi`).
2. Create the **Owner username** and **password**.
3. Set the **Administrator contact email**.
4. Immediately receive your server address and a terminal QR code to connect Penrill devices.

### Option B: Browser Setup Link

1. Run `sudo penrill-server setup` (or select **Finish setup in a web browser** in the interactive configurator) to print a one-time setup link and QR code.
2. Open the link in a browser on any device in the same local network and create the **owner** account.
3. **Verify the TLS certificate:** Because the server generates its own self-signed TLS certificate for local network encryption, your browser will show a one-time certificate warning. You can verify the certificate's short fingerprint (e.g. `3F9A-12C4-77B0`) on the setup page, in **Admin › Server**, or via `sudo penrill-server status`. The Penrill app pins and verifies this certificate automatically.

---

## Connecting Penrill

- **Desktop or Web App:** In Penrill, open **Sync › Your server**. Servers on the same local network appear automatically via mDNS/Avahi (`_penrill._tcp`), or you can enter the address shown on your server's home page. Penrill opens your browser to approve the device, so no password is typed into the editor.
- **Tablet or Phone:** Scan the QR code shown on your server's home page (or run `sudo penrill-server link`). The link opens `penrill.com/connect`, which launches the installed Penrill app or opens Penrill in the browser. Your server's address and certificate pin stay in the URL fragment (`#server=...`), so they are never sent to `penrill.com`.

---

## Inviting People & Managing Accounts

Registration is **invite-only** by default.

### Creating Invites

Create an invite link and QR code in the web interface under **Admin › People**, or from the terminal:

```sh
# Single-use invite, valid for 7 days
sudo penrill-server invite

# Classroom or team invite (25 uses, valid for 7 days)
sudo penrill-server invite --uses 25

# Administrator invite, valid for 14 days
sudo penrill-server invite --admin --days 14
```

### Password Resets & Legacy Boards

- **Forgotten password:** Generate a one-hour password reset link in **Admin › People › Reset password**, or run:
  ```sh
  sudo penrill-server reset-password USERNAME
  ```
- **Boards without an owner:** When an account is deleted, its boards stay; give them a new owner in **Admin › Boards**.

---

## Command-Line Interface & Interactive Control Center

Run `sudo penrill-server` (or `sudo penrill-server configure`) to open the **Interactive Control Center** with arrow-key navigation (`↑`/`↓`, `Enter`, `Esc`) for managing users, passwords, invites, devices, boards, network settings, backups, and uninstallation.

You can also run any command directly for quick actions or scripting:

| Command | Description |
| :--- | :--- |
| `sudo penrill-server` / `configure` | Launch the interactive terminal configurator & control panel |
| `sudo penrill-server status` | Show server name, version, address, TLS fingerprint, and user/board/device counts |
| `sudo penrill-server setup` | Print a new one-time setup link and QR code (while the server has no owner) |
| `sudo penrill-server link` | Print the server address, Penrill connect link, and QR code |
| `sudo penrill-server domain` | Configure a free `*.dedyn.io` domain (deSEC DDNS + auto Let's Encrypt SSL) or custom SSL certificate |
| `sudo penrill-server networks` | List or set active networks (LAN, Tailscale, mDNS, public domain) |
| `sudo penrill-server invite` | Create an invite link (`--admin`, `--uses N`, `--days N`) |
| `sudo penrill-server users` | List all accounts, roles, and statuses |
| `sudo penrill-server create-user USER` | Create an account directly (`--admin`, `--password PASS` or interactive prompt) |
| `sudo penrill-server set-password USER` | Set a new password immediately in the terminal (works for all accounts including owner) |
| `sudo penrill-server reset-password USER` | Generate a one-hour browser password reset link & QR code for `USER` |
| `sudo penrill-server delete-user USER` | Permanently delete a user account and revoke all its sessions |
| `sudo penrill-server role USER member\|admin` | Change an account's role between `member` and `admin` |
| `sudo penrill-server disable USER` | Disable an account and sign it out everywhere |
| `sudo penrill-server enable USER` | Re-enable a disabled account |
| `sudo penrill-server devices` | List linked devices, or revoke one with `--revoke ID` |
| `sudo penrill-server boards` | List all boards, reassign owner (`--owner ID USER`), or delete (`--delete ID`) |
| `sudo penrill-server rename NAME` | Change the server name displayed in Penrill editors |
| `sudo penrill-server email ADDRESS` | Update the administrator contact email |
| `sudo penrill-server backup [FILE]` | Export all boards, images, and accounts into a single `.zip` archive |
| `sudo penrill-server uninstall` | Uninstall Penrill Server (`--purge` to also delete all boards and data, `--yes` to skip prompt) |

---

## Service & Configuration

By default, the installed service listens on port **`5081`** with TLS on all network interfaces (`PENRILL_ADDRESS_MODE=lan`) and announces itself on the LAN as `_penrill._tcp` via Avahi.

### Paths & Logs

| Installation | Data Directory (persists across upgrades) | Admin Socket | Service Logs |
| :--- | :--- | :--- | :--- |
| **APT / `.deb`** | `/var/lib/penrill-server` | `/run/penrill-server/admin.sock` | `journalctl -u penrill-server -f` |

### Configuration Options

| Environment Variable | Default | Description |
| :--- | :--- | :--- |
| `PENRILL_PORT` | `5081` | TCP port the server listens on |
| `PENRILL_ADDRESS_MODE` | `lan` (`local` in dev) | `lan` enables HTTPS on all interfaces; `local` binds HTTP on loopback (e.g. behind a reverse proxy) |
| `PENRILL_EDITOR_ORIGINS` | `https://app.penrill.com,https://penrill.com` and local dev editors | Comma-separated CORS origins of extra web editors allowed to connect. They are added to the default list |
| `PENRILL_OPEN_REGISTRATION` | `false` | Set to `true` to allow open registration without invite links |
| `PENRILL_TRUST_PROXY` | `false` | Set to `true` behind a reverse proxy on this machine: believes its `X-Forwarded-For`/`-Proto` (from loopback only), so rate limits count each visitor instead of the proxy |
| `PENRILL_DATA_DIR` | `/var/lib/penrill-server` | Directory where the database (`penrill.db`), images (`assets`) and TLS keys are stored |
| `PENRILL_CONNECT_PAGE` | `https://penrill.com/connect` | Base URL used when generating device connect links |

- **On APT / systemd:** Run `sudo systemctl edit penrill-server` and add `Environment="PENRILL_EDITOR_ORIGINS=https://draw.example.com"` under `[Service]`, then `sudo systemctl restart penrill-server`.

---

## Packaging & Releases

### Local Package Builds

- **Debian packages (`.deb` for `amd64` and `arm64`):**
  ```sh
  sh packaging/build-deb.sh [VERSION]
  ```
  Requires the .NET 10 SDK and `dpkg-deb` (or Python 3 when cross-building on macOS). Outputs packages to `dist/`.
- **APT repository:**
  ```sh
  sh packaging/build-apt-repo.sh dist dist/apt-repo
  ```

### Automated Releases (GitHub Actions)

Pushing a tag matching `v*` (e.g. `git tag v0.3.0 && git push origin v0.3.0`) or running the **Release Packages (APT & Snap)** workflow manually in GitHub Actions will:

1. Run the test suite.
2. Build self-contained `.deb` packages for both `arm64` (Raspberry Pi) and `amd64`.
3. Publish a GitHub Release with the `.deb` and `SHA256SUMS` artifacts.
4. Build and deploy the APT repository to GitHub Pages.

Optional repository secrets:
- `GPG_PRIVATE_KEY` (and `GPG_PASSPHRASE` if encrypted): Signs the APT repository (`InRelease`, `Release.gpg`, and `penrill-archive-keyring.gpg`).
- `SNAPCRAFT_STORE_CREDENTIALS`: Exported via `snapcraft export-login` to publish builds to the Snap Store.

---

## Development

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```sh
# Run the server locally on http://127.0.0.1:5081 (SQLite data in src/Server/data)
dotnet run --project src/Server

# Or with the Aspire dashboard (logs, traces, metrics) and open registration
aspire run --project src/AppHost

# Run the test suite
dotnet test Penrill.slnx
```

The code follows the Clean Architecture solution template; start with [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
Its core (Domain, Application, Infrastructure, Web) is shared with Penrill Cloud as NuGet packages; see
[docs/EXTENDING.md](docs/EXTENDING.md).

MediatR is held at 12.5.0, its last Apache-2.0 release, and AutoMapper is not used; see
[ADR-008](docs/decisions/ADR-008-Free-MediatR-And-AutoMapper.md) before changing either.

### Web UI (`wwwroot`)

The server's web pages (setup, sign-in, device approval, account, and administration) are a React single-page app located in the main Penrill repository under `apps/server`, talking to the JSON API in `src/Server/Web`.

Its production build is committed to `src/Server/wwwroot`, so this repository builds and ships without requiring Node.js. To develop the web UI:

1. Check out the `penrill` repository next to `penrill-server`.
2. Start `penrill-server` (`dotnet run --project src/Server`).
3. In the `penrill` repository, run `npm run dev:server-ui` (opens on `http://localhost:5175`), and `npm run build:server-ui` to update `src/Server/wwwroot`.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Pull requests need a signed
[Contributor License Agreement](https://github.com/Penrill/penrill/blob/main/CLA.md); a bot asks for it on your first one.
