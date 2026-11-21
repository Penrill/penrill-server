#!/bin/sh
set -eu
repo=$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)
version=${1:-0.2.0}
dpkg --validate-version "$version"
out=${2:-"$repo/dist"}
mkdir -p "$out"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
pkg="$work/root"
install -d "$pkg/DEBIAN" "$pkg/usr/lib/penrill-server" "$pkg/usr/lib/systemd/system" "$pkg/usr/share/doc/penrill-server"
dotnet publish "$repo/src/Penrill.Server" -c Release -r linux-arm64 --self-contained true -p:DebugType=None -o "$pkg/usr/lib/penrill-server"
install -m 644 "$repo/LICENSE" "$pkg/usr/share/doc/penrill-server/copyright"
install -m 644 "$repo/packaging/penrill-server.service" "$pkg/usr/lib/systemd/system/"
install -m 755 "$repo/packaging/prerm" "$repo/packaging/postinst" "$pkg/DEBIAN/"
cat > "$pkg/DEBIAN/control" <<CONTROL
Package: penrill-server
Version: $version
Architecture: arm64
Maintainer: Yaroslav Yushchenko <contact@yaroslavyushchenko.com>
Depends: libc6 (>= 2.36), libgcc-s1, libstdc++6, libgssapi-krb5-2, libicu72 | libicu76, libssl3 | libssl3t64, zlib1g, systemd
Description: Board synchronization and sharing service for Penrill
CONTROL
dpkg-deb --root-owner-group --build "$pkg" "$out/penrill-server_${version}_arm64.deb"
