#!/bin/bash
# Sort out apt's "automatically installed and no longer required" list on Ubuntu 24.04.
#
# The list is split into groups instead of being removed blindly:
#   wine    32-bit (i386) libraries and their tools; kept (marked manual) while Wine is installed
#   inuse   libraries or Python modules some program outside apt still uses (/opt, /usr/local,
#           ~/.local, ...); kept (marked manual) - e.g. ROS built from source does not tell apt
#   dev     -dev/-doc/-tools packages for building software (PCL, VTK, Qt5, ...)
#   other   remaining runtime packages nothing uses any more
# "dev" and "other" are removed with --apply. Before removing, the package list is saved so
# --restore can put everything back.
#
# usage: ubuntu-tidy.sh             report only (changes nothing)
#        ubuntu-tidy.sh --apply     keep wine/inuse, remove dev/other (asks first)
#        ubuntu-tidy.sh --restore FILE
set -euo pipefail
STATE="${XDG_DATA_HOME:-$HOME/.local/share}/openshape/tidy"
SCAN_DIRS=(/opt /usr/local "$HOME/.local" "$HOME/ros2_ws" "$HOME/colcon_ws")

if [[ "${1:-}" == "--restore" ]]; then
    file="${2:?복원할 목록 파일을 지정하세요 ($STATE/*.removed)}"
    mapfile -t pkgs < "$file"
    sudo apt-get install -y "${pkgs[@]}"
    sudo apt-mark auto "${pkgs[@]}" >/dev/null
    echo "복원했습니다: ${#pkgs[@]}개"
    exit 0
fi

. /etc/os-release
[[ "$VERSION_ID" == "24.04" ]] || echo "주의: Ubuntu 24.04용으로 만들었습니다 (현재 $PRETTY_NAME)." >&2

mapfile -t auto < <(apt-get -s autoremove 2>/dev/null | awk '/^Remv /{print $2}')
if ((${#auto[@]} == 0)); then
    echo "정리할 패키지가 없습니다."
    exit 0
fi

wine_installed=0
dpkg-query -W -f='${Status}\n' wine-stable winehq-stable wine 2>/dev/null | grep -q "^install ok installed" && wine_installed=1

# shared libraries (by file name and ELF class) that programs outside apt need
declare -A needed=()
while IFS= read -r lib; do needed["$lib"]=1; done < <(
    for d in "${SCAN_DIRS[@]}"; do [[ -d "$d" ]] && find "$d" -xdev -type f \( -name '*.so*' -o -perm -u+x \) -size +1k 2>/dev/null; done |
    while IFS= read -r f; do
        head -c 4 "$f" 2>/dev/null | grep -q $'\x7fELF' || continue
        cls=$(od -An -tx1 -j4 -N1 "$f" | tr -d ' ')
        readelf -d "$f" 2>/dev/null | sed -n "s/.*(NEEDED).*\[\(.*\)\]/\1@$cls/p"
    done | sort -u)

declare -a g_wine=() g_inuse=() g_dev=() g_other=()
for p in "${auto[@]}"; do
    name="${p%%:*}"
    if [[ "$p" == *:i386 ]] && ((wine_installed)); then g_wine+=("$p"); continue; fi
    if [[ "$name" =~ (-dev|-doc|-dev-tools|-tools|-utils)$ || "$name" =~ ^(qdoc|qhelpgenerator|qtattributionsscanner|qt5-assistant|qttools5|qtdeclarative5-dev|google-mock|tcl-dev|tk-dev|tcl8|tk8) ]]; then
        g_dev+=("$p"); continue
    fi
    cls=02; [[ "$p" == *:i386 ]] && cls=01
    used=0
    while IFS= read -r so; do
        [[ -n "${needed["$(basename "$so")@$cls"]:-}" ]] && { used=1; break; }
    done < <(dpkg -L "$p" 2>/dev/null | grep -E '\.so(\.[0-9]+)*$')
    # Python modules: look for "import x" / "from x" in the scanned folders (e.g. ROS imports pydot)
    if ((!used)); then
        while IFS= read -r mod; do
            for d in "${SCAN_DIRS[@]}"; do
                [[ -d "$d" ]] && grep -rqsE --include='*.py' "^\s*(import|from) $mod\b" "$d" && { used=1; break 2; }
            done
        done < <(dpkg -L "$p" 2>/dev/null | sed -n 's#^/usr/lib/python3/dist-packages/\([A-Za-z_][A-Za-z0-9_]*\)\(/__init__\.py\|\.py\)$#\1#p' | sort -u)
    fi
    if ((used)); then g_inuse+=("$p"); else g_other+=("$p"); fi
done

show() { local title="$1"; shift; printf '\n[%s] %d개\n' "$title" "$#"; (($#)) && printf '  %s\n' "$@" | column -c 100 || true; }
echo "apt가 '더 이상 필요하지 않음'으로 표시한 패키지: ${#auto[@]}개"
show "유지 - Wine 32비트 (Wine 설치됨)" "${g_wine[@]}"
show "유지 - apt 밖의 프로그램이 사용 중" "${g_inuse[@]}"
show "제거 - 개발용 패키지 (PCL/VTK/Qt5 등 빌드할 때만 필요)" "${g_dev[@]}"
show "제거 - 아무것도 쓰지 않는 런타임" "${g_other[@]}"
remove=("${g_dev[@]}" "${g_other[@]}")
keep=("${g_wine[@]}" "${g_inuse[@]}")

if [[ "${1:-}" != "--apply" ]]; then
    printf '\n바뀐 것은 없습니다. 적용하려면: %s --apply\n' "$0"
    exit 0
fi

printf '\n%d개를 유지로 표시하고 %d개를 제거합니다. 계속할까요? [y/N] ' "${#keep[@]}" "${#remove[@]}"
read -r ans
[[ "$ans" == [yY]* ]] || { echo "취소했습니다."; exit 1; }
mkdir -p "$STATE"
stamp=$(date +%Y%m%d-%H%M%S)
((${#keep[@]})) && sudo apt-mark manual "${keep[@]}" >/dev/null && printf '%s\n' "${keep[@]}" > "$STATE/$stamp.kept"
if ((${#remove[@]})); then
    printf '%s\n' "${remove[@]}" > "$STATE/$stamp.removed"
    sudo apt-get remove -y "${remove[@]}"
fi
left=$(apt-get -s autoremove 2>/dev/null | grep -c '^Remv' || true)
echo "완료. 남은 '더 이상 필요하지 않음' 패키지: $left개"
[[ -f "$STATE/$stamp.removed" ]] && echo "되돌리기: $0 --restore $STATE/$stamp.removed"
