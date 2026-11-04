#!/bin/bash
set -euo pipefail

task_native_root="$(cd -- "$(dirname -- "$0")" && pwd)"
task_repo_root="$(cd -- "$task_native_root/../.." && pwd)"
task_bundle="$task_repo_root/Assets/Plugins/StandaloneFileBrowser/Plugins/StandaloneFileBrowser.bundle"
task_sdk="$(xcrun --show-sdk-path)"
task_build_dir="$(mktemp -d "${TMPDIR:-/tmp}/protobot-file-dialogs.XXXXXX")"
trap 'rm -rf -- "$task_build_dir"' EXIT

xcrun clang++ -std=c++17 -fobjc-arc -fblocks -O2 -bundle \
    -arch arm64 -arch x86_64 -mmacosx-version-min=12.0 -isysroot "$task_sdk" \
    -framework Cocoa -framework UniformTypeIdentifiers \
    "$task_native_root/StandaloneFileBrowser.mm" -o "$task_build_dir/StandaloneFileBrowser"
# Unity's source bundle contains .meta sidecars, which codesign treats as
# unsealed bundle-root contents. Sign the same clean layout Unity ships.
task_signed_bundle="$task_build_dir/StandaloneFileBrowser.bundle"
mkdir -p "$task_signed_bundle/Contents/MacOS"
cp "$task_bundle/Contents/Info.plist" "$task_signed_bundle/Contents/Info.plist"
cp "$task_build_dir/StandaloneFileBrowser" "$task_signed_bundle/Contents/MacOS/StandaloneFileBrowser"
codesign --force --sign - "$task_signed_bundle"
codesign --verify --strict "$task_signed_bundle"
cp "$task_signed_bundle/Contents/MacOS/StandaloneFileBrowser" "$task_bundle/Contents/MacOS/StandaloneFileBrowser"
cp "$task_signed_bundle/Contents/_CodeSignature/CodeResources" "$task_bundle/Contents/_CodeSignature/CodeResources"
lipo -archs "$task_bundle/Contents/MacOS/StandaloneFileBrowser"
