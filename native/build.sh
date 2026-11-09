#!/usr/bin/env bash
# Builds the native kernels into native/out (pricer_core.so). Called by Pricer.Native.csproj off Windows.
set -euo pipefail
root="$(cd "$(dirname "$0")" && pwd)"
configuration="${1:-Release}"
cmake -S "$root" -B "$root/build" -DCMAKE_BUILD_TYPE="$configuration"
cmake --build "$root/build" --config "$configuration"
