#!/bin/sh
# Builds the device renderers for a macOS or Linux app with the system C compiler:
#   libtdui_rp2040 / libtdui_esp32c3  (.dylib on macOS, .so elsewhere)
# Usage: hostui/build.sh [OUTDIR]      (default: hostui/out)
# Same sources and exports as build.bat (see tdui.h); plain ISO C11, no platform calls.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
root=$(dirname "$here")
out=${1:-$here/out}
mkdir -p "$out"
cc=${CC:-cc}
case "$(uname -s)" in
    Darwin) ext=dylib; shared="-dynamiclib" ;;
    *)      ext=so;    shared="-shared" ;;
esac

build() {   # board, source dir, extra flags, pages source
    src=$2
    $cc -std=c11 -O2 -fPIC -fvisibility=hidden $shared $3 -I"$src" -I"$here" -o "$out/libtdui_$1.$ext" \
        "$here/tdui.c" "$4" "$src/ui_sync.c" "$src/gfx.c" "$src/icons.c" \
        "$src/jig_lane.c" "$src/jig_paths.c" "$src/aa_fonts.c" -lm
}

build rp2040 "$root/src" "" "$root/src/ui_pages.c"
build esp32c3 "$root/esp32c3/src" "-DTDUI_FB_PTR" "$root/esp32c3/src/ui_pages.c"
# RP2350-Touch-LCD-1.28: the RP tree with the round pages (src/round), USB only.
build rp2350 "$root/src" "-DTD_BOARD_RP2350_128 -DTD_ROUND -DUI_USB_ONLY" "$root/src/round/ui_pages.c"
echo "built $out/libtdui_rp2040.$ext $out/libtdui_esp32c3.$ext $out/libtdui_rp2350.$ext"
