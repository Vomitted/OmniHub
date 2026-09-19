#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Vomitted
#
# Builds every Linux package OmniHub ships.
#
# "Every popular distro" turns out to be a smaller problem than it sounds, because the binary is
# self-contained: it carries its own .NET runtime and needs nothing but a kernel and glibc. So
# there is one build, and the packages differ only in where they put the file and how they
# describe it. That is why this is one script rather than five.
#
#   tar.gz    anything, including distros with no packaging at all
#   .deb      Debian, Ubuntu, Mint, Pop!_OS, Zorin, elementary, Raspberry Pi OS
#   .rpm      Fedora, RHEL, CentOS Stream, openSUSE, Nobara, Bazzite
#   PKGBUILD  Arch, Manjaro, EndeavourOS, Garuda  (for the AUR)
#
# Not shipped, and not through oversight:
#
#   Flatpak and Snap are sandboxed, and the sandbox is the point of them. Neither grants write
#   access to /sys/class/hwmon, which is the entire job here. A Flatpak of this would install
#   cleanly, start cleanly, and refuse every fan command -- worse than not existing, because it
#   would look like it worked.
#
#   AppImage would function, but an AppImage of a root daemon with a systemd unit is an awkward
#   shape: the tarball below does the same job without pretending to be portable.
#
# Usage:  ./packaging/build-packages.sh [version] [arch]
#         arch is x64 (default) or arm64

set -euo pipefail

VERSION="${1:-1.5.0}"
ARCH="${2:-x64}"

case "$ARCH" in
    x64)   RID=linux-x64;   DEB_ARCH=amd64;  RPM_ARCH=x86_64  ;;
    arm64) RID=linux-arm64; DEB_ARCH=arm64;  RPM_ARCH=aarch64 ;;
    *) echo "arch must be x64 or arm64, not '$ARCH'" >&2; exit 2 ;;
esac

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/dist"
STAGE="$OUT/stage-$ARCH"

rm -rf "$STAGE"
mkdir -p "$OUT"

echo "==> publishing $RID"
dotnet publish "$ROOT/OmniHub.Daemon/OmniHub.Daemon.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:Version="$VERSION" \
    -o "$OUT/publish-$ARCH" --nologo

# ---------------------------------------------------------------- filesystem layout
#
# Assembled once; every package below is a different way of wrapping it. The profiles directory
# ships EMPTY on purpose. A profile is the record that somebody verified a board, and shipping
# one for a board this project has never seen would be exactly the unearned claim the write gate
# exists to prevent.

install -Dm755 "$OUT/publish-$ARCH/omnihub"        "$STAGE/usr/bin/omnihub"
install -Dm644 "$ROOT/packaging/omnihub.service"   "$STAGE/usr/lib/systemd/system/omnihub.service"
install -Dm644 "$ROOT/LICENSE"                     "$STAGE/usr/share/licenses/omnihub/LICENSE"
install -Dm644 "$ROOT/NOTICE"                      "$STAGE/usr/share/licenses/omnihub/NOTICE"
install -dm755 "$STAGE/etc/omnihub/profiles"
install -dm755 "$STAGE/var/lib/omnihub"

# The measured default curve, written out so it can be edited rather than guessed at. These are
# the points the Windows build uses, measured on an HP Victus 15; the floor at 55 C exists
# because the stock firmware curve on that machine runs the fan at 0% well past it.
cat > "$STAGE/etc/omnihub/curve.json" <<'CURVE'
{
  "points": [
    { "tempC": 0,  "levelPercent": 0 },
    { "tempC": 55, "levelPercent": 15 },
    { "tempC": 65, "levelPercent": 30 },
    { "tempC": 75, "levelPercent": 50 },
    { "tempC": 85, "levelPercent": 75 },
    { "tempC": 90, "levelPercent": 100 }
  ]
}
CURVE

SIZE_KB=$(du -ks "$STAGE" | cut -f1)

# ---------------------------------------------------------------- tar.gz

echo "==> tar.gz"
tar -czf "$OUT/omnihub-$VERSION-$ARCH.tar.gz" -C "$STAGE" .

# ---------------------------------------------------------------- deb

if command -v dpkg-deb >/dev/null 2>&1; then
    echo "==> deb"
    DEB="$OUT/deb-$ARCH"
    rm -rf "$DEB"; cp -a "$STAGE" "$DEB"
    mkdir -p "$DEB/DEBIAN"

    cat > "$DEB/DEBIAN/control" <<CONTROL
Package: omnihub
Version: $VERSION
Section: utils
Priority: optional
Architecture: $DEB_ARCH
Maintainer: Vomitted <malvinxavierorlando01@gmail.com>
Installed-Size: $SIZE_KB
Homepage: https://github.com/Vomitted/OmniHub
Description: Fan and thermal control for laptops
 Runs a temperature-driven fan curve through the kernel's hardware monitoring
 class, for laptops whose firmware curve lets the fan idle while the machine
 is hot.
 .
 Reading works anywhere. Fan control stays refused until 'omnihub verify' has
 shown, on that exact board, that a commanded change actually moves the fan.
CONTROL

    # The curve is a conffile so dpkg will not overwrite one somebody has tuned. Profiles are
    # deliberately not listed: they are written by 'omnihub verify' rather than shipped, so
    # there is nothing for an upgrade to clobber.
    echo "/etc/omnihub/curve.json" > "$DEB/DEBIAN/conffiles"

    cat > "$DEB/DEBIAN/postinst" <<'POSTINST'
#!/bin/sh
set -e
if [ "$1" = "configure" ]; then
    systemctl daemon-reload >/dev/null 2>&1 || true
    echo ""
    echo "omnihub installed. Nothing is running yet, and nothing has been written to your hardware."
    echo ""
    echo "  omnihub probe          see what this machine exposes (no root, writes nothing)"
    echo "  sudo omnihub verify    prove fan control works on this board"
    echo "  sudo systemctl enable --now omnihub"
    echo ""
fi
POSTINST
    chmod 755 "$DEB/DEBIAN/postinst"

    cat > "$DEB/DEBIAN/prerm" <<'PRERM'
#!/bin/sh
set -e
# Stopped through systemd rather than killed: the daemon restores the fan controller on SIGTERM,
# and a removal that skipped that would leave the fan wherever the curve last put it, with the
# one program that could release it now uninstalled.
if [ -d /run/systemd/system ]; then
    systemctl stop omnihub.service >/dev/null 2>&1 || true
    systemctl disable omnihub.service >/dev/null 2>&1 || true
fi
PRERM
    chmod 755 "$DEB/DEBIAN/prerm"

    dpkg-deb --root-owner-group --build "$DEB" "$OUT/omnihub_${VERSION}_${DEB_ARCH}.deb" >/dev/null
else
    echo "==> deb skipped (dpkg-deb not installed)"
fi

# ---------------------------------------------------------------- rpm

if command -v rpmbuild >/dev/null 2>&1; then
    echo "==> rpm"
    RPMTOP="$OUT/rpm-$ARCH"
    rm -rf "$RPMTOP"; mkdir -p "$RPMTOP"/{BUILD,RPMS,SOURCES,SPECS,BUILDROOT}

    cat > "$RPMTOP/SPECS/omnihub.spec" <<SPEC
Name:           omnihub
Version:        $VERSION
Release:        1
Summary:        Fan and thermal control for laptops
License:        GPL-3.0-or-later
URL:            https://github.com/Vomitted/OmniHub
BuildArch:      $RPM_ARCH

# The binary carries its own runtime, so the automatic dependency scan finds shared objects that
# are bundled rather than required and generates dependencies no distro can satisfy. Nothing
# outside glibc is actually needed.
AutoReqProv:    no

%description
Runs a temperature-driven fan curve through the kernel's hardware monitoring class,
for laptops whose firmware curve lets the fan idle while the machine is hot.

Reading works anywhere. Fan control stays refused until 'omnihub verify' has shown,
on that exact board, that a commanded change actually moves the fan.

%install
cp -a $STAGE/. %{buildroot}/

%files
/usr/bin/omnihub
/usr/lib/systemd/system/omnihub.service
%license /usr/share/licenses/omnihub/LICENSE
/usr/share/licenses/omnihub/NOTICE
%config(noreplace) /etc/omnihub/curve.json
%dir /etc/omnihub/profiles
%dir /var/lib/omnihub

%post
systemctl daemon-reload >/dev/null 2>&1 || :

%preun
if [ \$1 -eq 0 ]; then
    systemctl stop omnihub.service >/dev/null 2>&1 || :
    systemctl disable omnihub.service >/dev/null 2>&1 || :
fi
SPEC

    rpmbuild --define "_topdir $RPMTOP" -bb "$RPMTOP/SPECS/omnihub.spec" >/dev/null
    cp "$RPMTOP"/RPMS/*/*.rpm "$OUT/"
else
    echo "==> rpm skipped (rpmbuild not installed)"
fi

# ---------------------------------------------------------------- PKGBUILD

echo "==> PKGBUILD"
cat > "$OUT/PKGBUILD" <<PKGBUILD
# Maintainer: Vomitted <malvinxavierorlando01@gmail.com>
pkgname=omnihub
pkgver=${VERSION}
pkgrel=1
pkgdesc="Fan and thermal control for laptops"
arch=('x86_64' 'aarch64')
url="https://github.com/Vomitted/OmniHub"
license=('GPL3')
depends=('glibc')
backup=('etc/omnihub/curve.json')
source_x86_64=("\$url/releases/download/v\$pkgver/omnihub-\$pkgver-x64.tar.gz")
source_aarch64=("\$url/releases/download/v\$pkgver/omnihub-\$pkgver-arm64.tar.gz")
sha256sums_x86_64=('SKIP')
sha256sums_aarch64=('SKIP')

package() {
    cp -a "\$srcdir/usr" "\$pkgdir/"
    cp -a "\$srcdir/etc" "\$pkgdir/"
    install -dm755 "\$pkgdir/var/lib/omnihub"
}
PKGBUILD

echo
echo "Built into $OUT:"
ls -1sh "$OUT" | grep -Ev '^total|publish-|stage-|deb-|rpm-' || true
echo
echo "Nothing here will touch a fan until 'omnihub verify' has passed on the machine it runs on."
