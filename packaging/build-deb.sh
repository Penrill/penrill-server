#!/bin/sh
# Builds penrill-server .deb packages. Usage: sh packaging/build-deb.sh [VERSION] [OUT_DIR] [ARCHES]
# ARCHES defaults to "amd64 arm64". Needs the .NET 10 SDK and dpkg-deb.
set -eu
repo=$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)
version=${1:-0.3.0}
dpkg --validate-version "$version"
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
    dpkg-deb --root-owner-group --build "$pkg" "$out/penrill-server_${version}_${arch}.deb"
    rm -rf "$work"
done
