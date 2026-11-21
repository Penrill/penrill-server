#!/bin/sh
# Builds penrill-server .deb packages. Usage: sh packaging/build-deb.sh [VERSION] [OUT_DIR] [ARCHES]
# ARCHES defaults to "amd64 arm64". Needs the .NET 10 SDK and dpkg-deb (or python3 on macOS).
set -eu
repo=$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)
version=${1:-0.3.0}
if command -v dpkg >/dev/null 2>&1; then
    dpkg --validate-version "$version"
fi
out=${2:-"$repo/dist"}
arches=${3:-"amd64 arm64"}
mkdir -p "$out"
for arch in $arches; do
    case "$arch" in
        amd64) rid=linux-x64 ;;
        arm64) rid=linux-arm64 ;;
        *) echo "Unsupported architecture: $arch" >&2; exit 2 ;;
    esac
    work=$(mktemp -d)
    pkg="$work/root"
    install -d "$pkg/DEBIAN" "$pkg/usr/lib/penrill-server" "$pkg/usr/lib/systemd/system" "$pkg/usr/share/doc/penrill-server" \
        "$pkg/usr/bin" "$pkg/etc/avahi/services"
    # Invariant globalization drops the libicu dependency, whose package name differs between every Debian and Ubuntu release.
    dotnet publish "$repo/src/Penrill.Server" -c Release -r "$rid" --self-contained true \
        -p:DebugType=None -p:InvariantGlobalization=true -p:Version="$version" -o "$pkg/usr/lib/penrill-server"
    ln -s ../lib/penrill-server/Penrill.Server "$pkg/usr/bin/penrill-server"
    install -m 644 "$repo/LICENSE" "$pkg/usr/share/doc/penrill-server/copyright"
    install -m 644 "$repo/packaging/penrill-server.service" "$pkg/usr/lib/systemd/system/"
    install -m 644 "$repo/packaging/penrill-server.avahi" "$pkg/etc/avahi/services/penrill-server.service"
    install -m 755 "$repo/packaging/prerm" "$repo/packaging/postinst" "$pkg/DEBIAN/"
    echo /etc/avahi/services/penrill-server.service > "$pkg/DEBIAN/conffiles"
    cat > "$pkg/DEBIAN/control" <<CONTROL
Package: penrill-server
Version: $version
Architecture: $arch
Maintainer: Yaroslav Yushchenko <contact@yaroslavyushchenko.com>
Depends: libc6 (>= 2.31), libgcc-s1, libstdc++6, libssl3 | libssl3t64, zlib1g, systemd
Recommends: avahi-daemon
Section: net
Priority: optional
Homepage: https://penrill.com
Description: Board synchronization and sharing server for Penrill
 Keeps Penrill boards in sync across your devices and lets you share them,
 on your own hardware. After installing, open the printed link to finish setup.
CONTROL
    deb="$out/penrill-server_${version}_${arch}.deb"
    if command -v dpkg-deb >/dev/null 2>&1; then
        dpkg-deb --root-owner-group --build "$pkg" "$deb"
    else
        python3 - "$pkg" "$deb" <<'PY'
import io, os, sys, tarfile, time

pkg, deb = sys.argv[1], sys.argv[2]
mtime = int(time.time())

def reset(ti):
    ti.uid = ti.gid = 0
    ti.uname = ti.gname = "root"
    ti.mtime = mtime
    return ti

def make_tar(root, skip=None):
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w:gz", format=tarfile.GNU_FORMAT) as tf:
        tf.add(root, arcname=".", recursive=False, filter=reset)
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames.sort()
            filenames.sort()
            if skip and dirpath == root and skip in dirnames:
                dirnames.remove(skip)
            for name in dirnames + filenames:
                full = os.path.join(dirpath, name)
                rel = "./" + os.path.relpath(full, root).replace(os.sep, "/")
                tf.add(full, arcname=rel, recursive=False, filter=reset)
    return buf.getvalue()

members = [
    ("debian-binary", b"2.0\n"),
    ("control.tar.gz", make_tar(os.path.join(pkg, "DEBIAN"))),
    ("data.tar.gz", make_tar(pkg, skip="DEBIAN")),
]
with open(deb, "wb") as f:
    f.write(b"!<arch>\n")
    for name, data in members:
        header = f"{name:<16}{mtime:<12}0     0     100644  {len(data):<10}`\n".encode("ascii")
        f.write(header)
        f.write(data)
        if len(data) % 2 == 1:
            f.write(b"\n")
print(f"dpkg-deb: building package 'penrill-server' in '{deb}'.")
PY
    fi
    rm -rf "$work"
done
