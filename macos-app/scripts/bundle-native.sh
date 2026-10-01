#!/bin/sh
# Xcode build phase (app target): puts the device renderers and the RP firmware into the app.
#   Contents/Frameworks/libtdui_{rp2040,esp32c3,rp2350}.dylib  <- ../hostui/build.sh (the boards' page code)
#   Contents/Resources/firmware/{manifest.json,*.uf2}         <- ../build/watch.uf2, ../build-rp2350/deck128.uf2
# The dylibs are built for the archs being built and signed with the app's identity, so the
# hardened runtime's library validation accepts them. Missing UF2s are skipped (no install offer).
set -eu
repo="$SRCROOT/.."
app="$TARGET_BUILD_DIR/$WRAPPER_NAME"
fw_dir="$app/Contents/Frameworks"
res_dir="$app/Contents/Resources/firmware"
mkdir -p "$fw_dir" "$res_dir"

arch_flags=""
for a in $ARCHS; do arch_flags="$arch_flags -arch $a"; done
CC="cc$arch_flags -mmacosx-version-min=$MACOSX_DEPLOYMENT_TARGET" sh "$repo/hostui/build.sh" "$fw_dir"

identity="${EXPANDED_CODE_SIGN_IDENTITY:--}"
stamp="--timestamp=none"
[ "$CONFIGURATION" = "Release" ] && [ "$identity" != "-" ] && stamp="--timestamp"   # notarization needs a secure timestamp
for lib in "$fw_dir"/libtdui_*.dylib; do
    codesign --force --sign "$identity" --options runtime $stamp "$lib"
done

# Firmware: version from src/version.h (both RP boards share it).
ver=$(sed -n 's/^#define FW_VERSION "\(.*\)"/\1/p' "$repo/src/version.h" | head -n 1)
entries=""
add() {   # board, source uf2
    if [ -f "$2" ]; then
        cp -X "$2" "$res_dir/$(basename "$2")"
        entries="$entries${entries:+,}
  {\"board\": \"$1\", \"version\": \"$ver\", \"file\": \"$(basename "$2")\"}"
    else
        echo "note: $2 not built; the app won't offer $1 firmware"
    fi
}
add rp2040-169 "$repo/build/watch.uf2"
add rp2350-128 "$repo/build-rp2350/deck128.uf2"
printf '[%s\n]\n' "$entries" > "$res_dir/manifest.json"
