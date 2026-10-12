#!/bin/sh
# Provide OpenCASCADE + libslvs headers and libraries for building.
# Uses the system copies when the -dev packages are installed, otherwise downloads the
# Ubuntu packages (no root needed) and unpacks them into build/sysroot.
# Prints the sysroot prefix ("/usr" or the unpacked tree).
set -eu
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
if [ -f /usr/include/opencascade/Standard.hxx ] && [ -f /usr/include/slvs.h ]; then
    echo /usr
    exit 0
fi
SYS="$ROOT/build-deps/sysroot"
if [ ! -f "$SYS/usr/include/opencascade/Standard.hxx" ]; then
    mkdir -p "$ROOT/build-deps/debs" "$SYS"
    cd "$ROOT/build-deps/debs"
    apt-get download \
        libocct-foundation-dev libocct-modeling-data-dev libocct-modeling-algorithms-dev libocct-data-exchange-dev \
        libocct-ocaf-dev libocct-visualization-dev \
        libocct-foundation-7.6t64 libocct-modeling-data-7.6t64 libocct-modeling-algorithms-7.6t64 libocct-data-exchange-7.6t64 \
        libocct-ocaf-7.6t64 libocct-visualization-7.6t64 libslvs1-dev libslvs1t64 >&2
    for d in *.deb; do dpkg-deb -x "$d" "$SYS"; done
fi
echo "$SYS/usr"
