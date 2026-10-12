#!/bin/sh
# Build libopenshape_occt.so against the OpenCASCADE found by packaging/fetch-occt.sh.
set -eu
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PREFIX="$(sh "$ROOT/packaging/fetch-occt.sh")"
LIBDIR="$PREFIX/lib/x86_64-linux-gnu"
OUT="$ROOT/occt/build"
mkdir -p "$OUT"
LIBS="-lTKernel -lTKMath -lTKG2d -lTKG3d -lTKGeomBase -lTKBRep -lTKGeomAlgo -lTKTopAlgo -lTKPrim -lTKBO -lTKBool -lTKFillet -lTKOffset -lTKMesh -lTKShHealing -lTKXSBase -lTKSTEP -lTKSTEPBase -lTKSTEPAttr -lTKSTEP209 -lTKIGES -lslvs"
g++ -O2 -std=c++17 -fPIC -shared -fvisibility=hidden -Wall -Wno-deprecated-declarations \
    "$ROOT/occt/kernel.cpp" -I"$PREFIX/include/opencascade" -I"$PREFIX/include" -L"$LIBDIR" -Wl,--as-needed $LIBS \
    -o "$OUT/libopenshape_occt.so"
echo "$OUT/libopenshape_occt.so"
