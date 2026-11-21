#!/bin/sh
# Builds an APT repository from .deb packages in DEB_DIR.
# Usage: sh packaging/build-apt-repo.sh [DEB_DIR] [OUT_DIR] [REPO_URL]
# Optional environment variables for signing: GPG_PRIVATE_KEY (and GPG_PASSPHRASE) or GPG_KEY_ID.
set -eu

repo=$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)
deb_dir=${1:-"$repo/dist"}
out=${2:-"$repo/dist/apt-repo"}
repo_url=${3:-"https://penrill.github.io/penrill-server"}

rm -rf "$out"
mkdir -p "$out/pool/main/p/penrill-server" \
    "$out/dists/stable/main/binary-amd64" \
    "$out/dists/stable/main/binary-arm64"

found=0
for deb in "$deb_dir"/penrill-server_*_*.deb; do
    [ -f "$deb" ] || continue
    cp "$deb" "$out/pool/main/p/penrill-server/"
    found=1
done

if [ "$found" -eq 0 ]; then
    echo "No penrill-server .deb packages found in $deb_dir" >&2
    exit 1
fi

python3 - "$out" <<'PY'
import datetime, gzip, hashlib, io, os, shutil, subprocess, sys, tarfile

out = sys.argv[1]
arches = ["amd64", "arm64"]

def extract_control(deb_path):
    if shutil.which("dpkg-deb"):
        res = subprocess.run(["dpkg-deb", "-f", deb_path], capture_output=True, text=True, check=True)
        return res.stdout.strip()
    with open(deb_path, "rb") as f:
        if f.read(8) != b"!<arch>\n":
            raise ValueError(f"Not an ar archive: {deb_path}")
        while True:
            header = f.read(60)
            if len(header) < 60:
                break
            name = header[:16].decode("ascii").strip().rstrip("/")
            size = int(header[48:58].decode("ascii").strip())
            data = f.read(size)
            if size % 2 == 1:
                f.read(1)
            if name.startswith("control.tar"):
                if name.endswith(".zst"):
                    proc = subprocess.run(["zstd", "-dc"], input=data, capture_output=True, check=True)
                    raw = proc.stdout
                    tf = tarfile.open(fileobj=io.BytesIO(raw), mode="r:")
                else:
                    tf = tarfile.open(fileobj=io.BytesIO(data), mode="r:*")
                with tf:
                    for member in tf.getmembers():
                        if os.path.basename(member.name) == "control":
                            return tf.extractfile(member).read().decode("utf-8").strip()
    raise ValueError(f"No control file found in {deb_path}")

def hashes(data):
    return (
        hashlib.md5(data).hexdigest(),
        hashlib.sha1(data).hexdigest(),
        hashlib.sha256(data).hexdigest(),
    )

pool_dir = os.path.join(out, "pool", "main", "p", "penrill-server")
debs = sorted(f for f in os.listdir(pool_dir) if f.endswith(".deb"))

for arch in arches:
    entries = []
    for fname in debs:
        if not fname.endswith(f"_{arch}.deb"):
            continue
        full = os.path.join(pool_dir, fname)
        rel = f"pool/main/p/penrill-server/{fname}"
        with open(full, "rb") as f:
            raw = f.read()
        md5, sha1, sha256 = hashes(raw)
        ctrl = extract_control(full)
        entry = (
            f"{ctrl}\n"
            f"Filename: {rel}\n"
            f"Size: {len(raw)}\n"
            f"MD5sum: {md5}\n"
            f"SHA1: {sha1}\n"
            f"SHA256: {sha256}\n"
        )
        entries.append(entry)
    pkg_text = "\n".join(entries) + ("\n" if entries else "")
    pkg_bytes = pkg_text.encode("utf-8")
    bin_dir = os.path.join(out, "dists", "stable", "main", f"binary-{arch}")
    pkg_path = os.path.join(bin_dir, "Packages")
    with open(pkg_path, "wb") as f:
        f.write(pkg_bytes)
    with gzip.GzipFile(filename="", mode="wb", fileobj=open(pkg_path + ".gz", "wb"), mtime=0) as gz:
        gz.write(pkg_bytes)

dist_dir = os.path.join(out, "dists", "stable")
index_files = []
for arch in arches:
    for suffix in ("Packages", "Packages.gz"):
        rel = f"main/binary-{arch}/{suffix}"
        full = os.path.join(dist_dir, rel)
        with open(full, "rb") as f:
            raw = f.read()
        md5, sha1, sha256 = hashes(raw)
        index_files.append((rel, len(raw), md5, sha1, sha256))

now = datetime.datetime.now(datetime.timezone.utc).strftime("%a, %d %b %Y %H:%M:%S UTC")
release_lines = [
    "Origin: Penrill",
    "Label: Penrill",
    "Suite: stable",
    "Codename: stable",
    f"Date: {now}",
    "Architectures: amd64 arm64",
    "Components: main",
    "Description: Penrill Server APT Repository",
    "MD5Sum:",
]
for rel, size, md5, _, _ in index_files:
    release_lines.append(f" {md5} {size:16d} {rel}")
release_lines.append("SHA1:")
for rel, size, _, sha1, _ in index_files:
    release_lines.append(f" {sha1} {size:16d} {rel}")
release_lines.append("SHA256:")
for rel, size, _, _, sha256 in index_files:
    release_lines.append(f" {sha256} {size:16d} {rel}")

with open(os.path.join(dist_dir, "Release"), "w", encoding="utf-8") as f:
    f.write("\n".join(release_lines) + "\n")
PY

signed=0
if [ -n "${GPG_PRIVATE_KEY:-}" ] || [ -n "${GPG_KEY_ID:-}" ]; then
    gnupg_home=""
    if [ -n "${GPG_PRIVATE_KEY:-}" ]; then
        gnupg_home=$(mktemp -d)
        chmod 700 "$gnupg_home"
        export GNUPGHOME="$gnupg_home"
        printf '%s\n' "$GPG_PRIVATE_KEY" | gpg --batch --import
        key_id=$(gpg --batch --list-secret-keys --with-colons | awk -F: '/^sec:/ { print $5; exit }')
    else
        key_id="$GPG_KEY_ID"
    fi

    gpg_opts="--batch --yes --default-key $key_id"
    if [ -n "${GPG_PASSPHRASE:-}" ]; then
        gpg_opts="$gpg_opts --pinentry-mode loopback --passphrase $GPG_PASSPHRASE"
    fi

    gpg --batch --yes --export "$key_id" | gpg --batch --yes --dearmor -o "$out/penrill-archive-keyring.gpg"
    # shellcheck disable=SC2086
    gpg $gpg_opts --armor --detach-sign -o "$out/dists/stable/Release.gpg" "$out/dists/stable/Release"
    # shellcheck disable=SC2086
    gpg $gpg_opts --clearsign -o "$out/dists/stable/InRelease" "$out/dists/stable/Release"

    [ -n "$gnupg_home" ] && rm -rf "$gnupg_home"
    signed=1
fi

: > "$out/.nojekyll"

if [ "$signed" -eq 1 ]; then
    cat > "$out/install.sh" <<INSTALL
#!/bin/sh
set -eu
mkdir -p /usr/share/keyrings
curl -fsSL $repo_url/penrill-archive-keyring.gpg -o /usr/share/keyrings/penrill-archive-keyring.gpg
chmod 644 /usr/share/keyrings/penrill-archive-keyring.gpg
echo "deb [signed-by=/usr/share/keyrings/penrill-archive-keyring.gpg] $repo_url stable main" > /etc/apt/sources.list.d/penrill.list
apt-get update
apt-get install -y penrill-server
INSTALL
else
    cat > "$out/install.sh" <<INSTALL
#!/bin/sh
set -eu
if curl -fsSLI $repo_url/penrill-archive-keyring.gpg >/dev/null 2>&1; then
    mkdir -p /usr/share/keyrings
    curl -fsSL $repo_url/penrill-archive-keyring.gpg -o /usr/share/keyrings/penrill-archive-keyring.gpg
    chmod 644 /usr/share/keyrings/penrill-archive-keyring.gpg
    echo "deb [signed-by=/usr/share/keyrings/penrill-archive-keyring.gpg] $repo_url stable main" > /etc/apt/sources.list.d/penrill.list
else
    echo "deb [trusted=yes] $repo_url stable main" > /etc/apt/sources.list.d/penrill.list
fi
apt-get update
apt-get install -y penrill-server
INSTALL
fi
chmod +x "$out/install.sh"

cat > "$out/index.html" <<HTML
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Penrill Server Packages</title>
  <style>
    body { font-family: system-ui, -apple-system, sans-serif; max-width: 720px; margin: 3rem auto; padding: 0 1.25rem; line-height: 1.6; color: #18181b; }
    pre { background: #f4f4f5; padding: 1rem; border-radius: 8px; overflow-x: auto; }
    code { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 0.9em; }
  </style>
</head>
<body>
  <h1>Penrill Server APT Repository</h1>
  <p>Install Penrill Server on Raspberry Pi OS, Debian, or Ubuntu (64-bit, <code>arm64</code> or <code>amd64</code>):</p>
  <pre><code>curl -fsSL $repo_url/install.sh | sudo sh</code></pre>
  <p>Or install via Snap:</p>
  <pre><code>sudo snap install penrill-server
sudo penrill-server setup</code></pre>
  <p>Source and documentation: <a href="https://github.com/Penrill/penrill-server">github.com/Penrill/penrill-server</a></p>
</body>
</html>
HTML

echo "APT repository built in $out"
