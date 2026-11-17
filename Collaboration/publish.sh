#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
rid="${1:-osx-arm64}"
case "$rid" in osx-arm64|win-x64|linux-x64|linux-arm64) ;; *) echo "Supported RIDs: osx-arm64, win-x64, linux-x64, linux-arm64" >&2; exit 1 ;; esac
dotnet publish src/Cli/Cli.csproj -c Release -r "$rid" --self-contained true -p:CollaborationRuntime="$rid" -p:RestoreLockedMode=true -p:UseSharedCompilation=false -m:1 -nr:false -o "artifacts/$rid"
cp ../Assets/Resources/Collaboration/catalog.json "artifacts/$rid/catalog.json"
cp ../LICENSE "artifacts/$rid/LICENSE-Protobot.txt"
cp THIRD-PARTY-NOTICES.md "artifacts/$rid/"
cp NETWORK.md "artifacts/$rid/"
cp TLS.md "artifacts/$rid/"
cp ROOM_MANAGEMENT.md "artifacts/$rid/"
cp ROOM_REPLACEMENT.md "artifacts/$rid/"
cp INSTALLATION.md "artifacts/$rid/"
cp licenses/*.txt "artifacts/$rid/"
mkdir -p "artifacts/$rid/deploy"
cp deploy/* "artifacts/$rid/deploy/"
dotnet_root="$(dirname "$(command -v dotnet)")"
if [ ! -f "$dotnet_root/LICENSE.txt" ] && [ -n "${DOTNET_ROOT:-}" ]; then dotnet_root="$DOTNET_ROOT"; fi
cp "$dotnet_root/LICENSE.txt" "artifacts/$rid/LICENSE-dotnet.txt"
cp "$dotnet_root/ThirdPartyNotices.txt" "artifacts/$rid/NOTICES-dotnet.txt"
if [[ "$rid" == osx-* ]] && command -v codesign >/dev/null; then
  codesign --force --sign - "artifacts/$rid/protobot-collab"
  codesign --verify --strict "artifacts/$rid/protobot-collab"
fi
# The manifest is generated after signing and includes every distributed byte.
# Remove only this known generated file when republishing this task-owned output.
rm -f "artifacts/$rid/artifact.json"
dotnet build src/Cli/Cli.csproj -c Release --no-restore -p:UseSharedCompilation=false -m:1 -nr:false
dotnet src/Cli/bin/Release/net10.0/protobot-collab.dll artifact-manifest --artifact "artifacts/$rid" --runtime "$rid"
