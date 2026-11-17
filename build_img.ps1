$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$Dotnet = Join-Path $Root ".tooling\dotnet\dotnet.exe"
$Out = Join-Path $Root "Release"
$env:MSYS = $null
$env:MSYSTEM = $null

if (-not (Test-Path $Dotnet)) { Write-Error "dotnet fehlt: $Dotnet"; exit 1 }

& $Dotnet publish (Join-Path $Root "WellenEditor.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=embedded -o $Out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Build OK: $Out\wavebenchpro.exe"