#!/bin/sh
# Install an OpenShape .deb on Ubuntu 24.04 without apt's side notes:
#  - "N: Download is performed unsandboxed as root ... couldn't be accessed by user '_apt'":
#    home folders are 750 on 24.04, so the package is copied to a folder _apt can read first.
#  - "The following packages were automatically installed and are no longer required": hidden
#    (APT::Get::HideAutoRemove); see tools/ubuntu-tidy.sh to deal with that list itself.
# usage: sh install-deb.sh [path/to/openshape_X.Y.Z_amd64.deb]   (default: newest in dist/)
set -eu
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEB="${1:-$(ls -1v "$ROOT"/dist/openshape_*_amd64.deb 2>/dev/null | tail -n 1)}"
[ -n "$DEB" ] && [ -f "$DEB" ] || { echo "설치할 .deb가 없습니다. 먼저 sh packaging/build-deb.sh를 실행하세요." >&2; exit 1; }

STAGE="$(mktemp -d /tmp/openshape-deb.XXXXXX)"
trap 'rm -rf "$STAGE"' EXIT
cp "$DEB" "$STAGE/"
chmod 755 "$STAGE"
chmod 644 "$STAGE/$(basename "$DEB")"

echo "설치: $(basename "$DEB")"
sudo apt-get install -y -q -o APT::Get::HideAutoRemove=1 --allow-downgrades "$STAGE/$(basename "$DEB")" \
    | grep -v -E "^(Reading|Building|패키지 목록|의존성 트리|상태 정보|주의, )" || true
dpkg-query -W -f='설치됨: ${Package} ${Version}\n' openshape
