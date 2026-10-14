#!/bin/sh
# Build the Rust engine and the self-contained C# app, run the tests, then package a .deb.
set -eu
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION=1.3.0
ARCH=amd64
PKG=openshape_${VERSION}_${ARCH}
STAGE="$ROOT/build/$PKG"

cargo build --release --manifest-path "$ROOT/engine/Cargo.toml"
cargo test --release --manifest-path "$ROOT/engine/Cargo.toml"
# Rust core of the client downloader (SHA-256 check, dpkg version order)
cargo test --release --manifest-path "$ROOT/installer/native/Cargo.toml"
cargo build --release --manifest-path "$ROOT/installer/native/Cargo.toml"

# OpenCASCADE shim (headers/libs from the system or from downloaded Ubuntu packages)
sh "$ROOT/occt/build.sh"
OCCT_PREFIX="$(sh "$ROOT/packaging/fetch-occt.sh")"
export LD_LIBRARY_PATH="$OCCT_PREFIX/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

# headless UI walkthrough: drives the real window and must pass before packaging
dotnet build "$ROOT/uitest/UiTest.csproj" -c Release
rm -rf "$ROOT/build-uitest"
# isolated settings so the walkthrough never touches the user's recent-files list
XDG_CONFIG_HOME="$ROOT/build-uitest/config" dotnet "$ROOT/uitest/bin/Release/net10.0/UiTest.dll" "$ROOT/build-uitest"

rm -rf "$ROOT/build" && mkdir -p "$STAGE/DEBIAN" "$STAGE/opt/openshape" "$STAGE/usr/bin" \
    "$STAGE/usr/share/applications" "$STAGE/usr/share/icons/hicolor/256x256/apps" "$STAGE/usr/share/mime/packages"
dotnet publish "$ROOT/app/OpenShape.csproj" -c Release -r linux-x64 --self-contained true \
    -p:DebugType=none -o "$STAGE/opt/openshape"

"$STAGE/opt/openshape/openshape" --export-icon "$STAGE/opt/openshape/openshape.png"
cp "$STAGE/opt/openshape/openshape.png" "$STAGE/usr/share/icons/hicolor/256x256/apps/openshape.png"
ln -s ../../opt/openshape/openshape "$STAGE/usr/bin/openshape"
# streaming installer / updater (same role as Autodesk's "Fusion Client Downloader")
dotnet publish "$ROOT/installer/Installer.csproj" -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none -o "$ROOT/build/installer"
install -m 755 "$ROOT/build/installer/openshape-installer" "$STAGE/opt/openshape/openshape-installer"

cat > "$STAGE/usr/share/applications/openshape.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=OpenShape CAD
GenericName=3D CAD
GenericName[ko]=3D CAD
Comment=Parametric 3D CAD for Ubuntu (C# + Rust)
Comment[ko]=Ubuntu용 파라메트릭 3D CAD (C# + Rust)
Exec=openshape %f
Icon=openshape
Terminal=false
Categories=Graphics;3DGraphics;Engineering;
MimeType=application/x-openshape-design;
StartupWMClass=openshape
DESKTOP

cat > "$STAGE/usr/share/applications/openshape-update.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=OpenShape CAD Update
Name[ko]=OpenShape CAD 업데이트
Comment=Download and install the latest OpenShape CAD
Comment[ko]=최신 OpenShape CAD를 내려받아 설치합니다
Exec=/opt/openshape/openshape-installer
Icon=openshape
Terminal=false
Categories=Graphics;Settings;
DESKTOP

cat > "$STAGE/usr/share/mime/packages/openshape.xml" <<MIME
<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
  <mime-type type="application/x-openshape-design">
    <comment>OpenShape CAD design</comment>
    <comment xml:lang="ko">OpenShape CAD 디자인</comment>
    <glob pattern="*.osc"/>
  </mime-type>
</mime-info>
MIME

SIZE=$(du -sk "$STAGE" | cut -f1)
cat > "$STAGE/DEBIAN/control" <<CONTROL
Package: openshape
Version: $VERSION
Section: graphics
Priority: optional
Architecture: $ARCH
Installed-Size: $SIZE
Depends: libc6, libgcc-s1, libstdc++6, libx11-6, libice6, libsm6, libfontconfig1, libocct-foundation-7.6t64, libocct-modeling-data-7.6t64, libocct-modeling-algorithms-7.6t64, libocct-data-exchange-7.6t64, libslvs1t64
Maintainer: a71143055 <a71143055@gmail.com>
Description: Parametric 3D CAD for Ubuntu
 OpenShape CAD is a clean-room parametric modeller whose screen layout
 follows Autodesk Fusion (toolbar, browser, timeline, ViewCube, navigation
 bar): sketches (also on model faces), extrude, revolve, loft, sweep,
 primitives, surfaces (extrude, patch, thicken), sheet metal flanges,
 plastic ribs and bosses, press/pull, section analysis, parameters, sketch
 trim, tangent/symmetric constraints, flat patterns with DXF export, data
 panel with recent designs, built-in IsaacSimLink (send geometry or URDF robots to Isaac Sim),
 booleans, fillet, chamfer, shell, sketch dimensions and constraints
 (SolveSpace solver), components and rigid/revolute/slider
 joints, move, mirror, patterns, STEP/IGES import,
 STEP/STL/OBJ/USD export (USD with UsdPhysics joints for Isaac Sim). UI in C# (Avalonia), B-rep geometry by
 OpenCASCADE, display and mesh I/O in Rust.
 Runs natively on Ubuntu (no Wine, no Windows components).
 Contains no Autodesk code or assets.
CONTROL
cat > "$STAGE/DEBIAN/postinst" <<'POST'
#!/bin/sh
set -e
command -v update-mime-database >/dev/null && update-mime-database /usr/share/mime || true
command -v update-desktop-database >/dev/null && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q /usr/share/icons/hicolor || true
POST
chmod 755 "$STAGE/DEBIAN/postinst"

chmod -R u+rwX,go+rX,go-w "$STAGE"
mkdir -p "$ROOT/dist"
dpkg-deb --root-owner-group --build "$STAGE" "$ROOT/dist/$PKG.deb"
install -m 755 "$ROOT/build/installer/openshape-installer" "$ROOT/dist/OpenShape-Client-Downloader"
echo "built: $ROOT/dist/$PKG.deb and $ROOT/dist/OpenShape-Client-Downloader"
