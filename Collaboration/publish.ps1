param([ValidateSet('osx-arm64', 'win-x64', 'linux-x64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet publish src/Cli/Cli.csproj -c Release -r $Runtime --self-contained true "-p:CollaborationRuntime=$Runtime" -p:RestoreLockedMode=true -m:1 -o "artifacts/$Runtime"
    if ($LASTEXITCODE -ne 0) { throw 'Collaboration helper publication failed.' }
    Copy-Item ../LICENSE "artifacts/$Runtime/LICENSE-Protobot.txt"
    Copy-Item THIRD-PARTY-NOTICES.md "artifacts/$Runtime/"
    Copy-Item licenses/*.txt "artifacts/$Runtime/"
    New-Item -ItemType Directory -Force "artifacts/$Runtime/deploy" | Out-Null
    Copy-Item deploy/* "artifacts/$Runtime/deploy/"
    $runtimeRoot = Split-Path (Get-Command dotnet).Source
    if (!(Test-Path "$runtimeRoot/LICENSE.txt") -and $env:DOTNET_ROOT) { $runtimeRoot = $env:DOTNET_ROOT }
    Copy-Item "$runtimeRoot/LICENSE.txt" "artifacts/$Runtime/LICENSE-dotnet.txt"
    Copy-Item "$runtimeRoot/ThirdPartyNotices.txt" "artifacts/$Runtime/NOTICES-dotnet.txt"
} finally { Pop-Location }
