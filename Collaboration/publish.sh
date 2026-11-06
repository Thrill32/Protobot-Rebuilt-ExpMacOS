#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
rid="${1:-osx-arm64}"
case "$rid" in osx-arm64|win-x64|linux-x64) ;; *) echo "Supported RIDs: osx-arm64, win-x64, linux-x64" >&2; exit 1 ;; esac
dotnet publish src/Cli/Cli.csproj -c Release -r "$rid" --self-contained true -o "artifacts/$rid"
cp ../LICENSE "artifacts/$rid/LICENSE-Protobot.txt"
cp THIRD-PARTY-NOTICES.md "artifacts/$rid/"
dotnet_root="$(dirname "$(command -v dotnet)")"
if [ ! -f "$dotnet_root/LICENSE.txt" ] && [ -n "${DOTNET_ROOT:-}" ]; then dotnet_root="$DOTNET_ROOT"; fi
cp "$dotnet_root/LICENSE.txt" "artifacts/$rid/LICENSE-dotnet.txt"
cp "$dotnet_root/ThirdPartyNotices.txt" "artifacts/$rid/NOTICES-dotnet.txt"
if [[ "$rid" == osx-* ]] && command -v codesign >/dev/null; then
  codesign --force --sign - "artifacts/$rid/protobot-collab"
  codesign --verify --strict "artifacts/$rid/protobot-collab"
fi
