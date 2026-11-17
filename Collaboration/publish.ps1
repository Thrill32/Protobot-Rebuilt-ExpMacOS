param([ValidateSet('osx-arm64', 'win-x64', 'linux-x64', 'linux-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet publish src/Cli/Cli.csproj -c Release -r $Runtime --self-contained true "-p:CollaborationRuntime=$Runtime" -p:RestoreLockedMode=true -p:UseSharedCompilation=false -m:1 -nr:false -o "artifacts/$Runtime"
    if ($LASTEXITCODE -ne 0) { throw 'Collaboration helper publication failed.' }
    Copy-Item ../Assets/Resources/Collaboration/catalog.json "artifacts/$Runtime/catalog.json"
    Copy-Item ../LICENSE "artifacts/$Runtime/LICENSE-Protobot.txt"
    Copy-Item THIRD-PARTY-NOTICES.md "artifacts/$Runtime/"
    Copy-Item NETWORK.md "artifacts/$Runtime/"
    Copy-Item TLS.md "artifacts/$Runtime/"
    Copy-Item ROOM_MANAGEMENT.md "artifacts/$Runtime/"
    Copy-Item ROOM_REPLACEMENT.md "artifacts/$Runtime/"
    Copy-Item INSTALLATION.md "artifacts/$Runtime/"
    Copy-Item licenses/*.txt "artifacts/$Runtime/"
    New-Item -ItemType Directory -Force "artifacts/$Runtime/deploy" | Out-Null
    Copy-Item deploy/* "artifacts/$Runtime/deploy/"
    $runtimeRoot = Split-Path (Get-Command dotnet).Source
    if (!(Test-Path "$runtimeRoot/LICENSE.txt") -and $env:DOTNET_ROOT) { $runtimeRoot = $env:DOTNET_ROOT }
    Copy-Item "$runtimeRoot/LICENSE.txt" "artifacts/$Runtime/LICENSE-dotnet.txt"
    Copy-Item "$runtimeRoot/ThirdPartyNotices.txt" "artifacts/$Runtime/NOTICES-dotnet.txt"
    Remove-Item "artifacts/$Runtime/artifact.json" -ErrorAction SilentlyContinue
    dotnet build src/Cli/Cli.csproj -c Release --no-restore -p:UseSharedCompilation=false -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Collaboration manifest tool build failed.' }
    dotnet src/Cli/bin/Release/net10.0/protobot-collab.dll artifact-manifest --artifact "artifacts/$Runtime" --runtime $Runtime
    if ($LASTEXITCODE -ne 0) { throw 'Collaboration artifact manifest failed.' }
} finally { Pop-Location }
