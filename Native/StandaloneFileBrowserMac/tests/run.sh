#!/bin/bash
set -euo pipefail
task_test_root="$(cd -- "$(dirname -- "$0")" && pwd)"
task_test_dir="$(mktemp -d "${TMPDIR:-/tmp}/protobot-dialog-test.XXXXXX")"
trap 'rm -rf -- "$task_test_dir"' EXIT
xcrun clang++ -std=c++17 -fobjc-arc -fblocks -O2 -mmacosx-version-min=12.0 \
    -framework Cocoa -framework UniformTypeIdentifiers \
    "$task_test_root/Smoke.mm" -o "$task_test_dir/Smoke"
"$task_test_dir/Smoke"
