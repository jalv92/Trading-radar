#!/usr/bin/env bash
# Stage a Custom/ mirror (Engine + NinjaTrader sources) for nt8c compile validation.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STAGE="${1:-$ROOT/build/.stage/Custom}"
rm -rf "$STAGE"
mkdir -p "$STAGE/AddOns/LiquidityRadar"
cp "$ROOT"/Engine/*.cs       "$STAGE/AddOns/LiquidityRadar/"
cp "$ROOT"/NinjaTrader/*.cs   "$STAGE/AddOns/LiquidityRadar/"
echo "staged -> $STAGE"
