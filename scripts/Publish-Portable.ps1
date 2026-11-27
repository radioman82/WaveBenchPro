param(
    [string]$Dotnet = "dotnet",
    [string]$OutputDirectory
)
$ErrorActionPreference = "Stop"
function Get-Sha256([string]$Path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $inputStream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($algorithm.ComputeHash($inputStream)).Replace("-", "").ToLowerInvariant() }
    finally { $inputStream.Dispose(); $algorithm.Dispose() }
}
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot "WellenEditor.csproj")
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if (-not $version) { throw "Application version is missing." }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "artifacts\$version" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw "Use a fresh output directory: $OutputDirectory" }
$status = & git -C $projectRoot status --porcelain
if ($LASTEXITCODE -ne 0 -or $status) { throw "Commit source changes before packaging." }
$commit = (& git -C $projectRoot rev-parse HEAD).Trim()
$publish = Join-Path $OutputDirectory "WaveBenchPro"
New-Item -ItemType Directory -Path $publish -Force | Out-Null
& $Dotnet restore (Join-Path $projectRoot "WellenEditor.csproj") --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Restore failed." }
& $Dotnet publish (Join-Path $projectRoot "WellenEditor.csproj") -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:ContinuousIntegrationBuild=true "-p:OutputPath=$(Join-Path $OutputDirectory 'build')" -o $publish
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
# Verify that the publish target emitted each codec as a sidecar from the locked packages.
$assets = Get-Content -LiteralPath (Join-Path $projectRoot "obj\project.assets.json") -Raw | ConvertFrom-Json
foreach ($dependency in @("NAudio.Lame/2.1.0", "org.sndfile.runtime.win-x64/1.2.2")) {
    $library = $assets.libraries.PSObject.Properties[$dependency].Value
    $package = $null
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $folder $library.path
        if (Test-Path -LiteralPath $candidate) { $package = $candidate; break }
    }
    if (-not $package) { throw "Restored codec package is missing: $dependency" }
    $nativeFiles = @(Get-ChildItem -LiteralPath $package -Filter *.dll -Recurse | Where-Object {
        $_.Name -eq "libmp3lame.64.dll" -or $_.FullName -match '[\\/]runtimes[\\/]win-x64[\\/]native[\\/]'
    })
    if ($nativeFiles.Count -eq 0) { throw "Native codec files are missing: $dependency" }
    foreach ($file in $nativeFiles) {
        $sidecar = Join-Path $publish $file.Name
        if (-not (Test-Path -LiteralPath $sidecar -PathType Leaf)) { throw "Published codec sidecar is missing: $($file.Name)" }
        if ((Get-Sha256 $sidecar) -ne (Get-Sha256 $file.FullName)) {
            throw "Published codec does not match the locked package: $($file.Name)"
        }
    }
}
foreach ($name in @("LICENSE", "README.md", "README.de.md", "CHANGELOG.md", "THIRD-PARTY-NOTICES.md", "global.json", "packages.lock.json")) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $publish
}
foreach ($name in @("docs", "licenses")) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $publish -Recurse
}
foreach ($file in (Get-ChildItem -LiteralPath $publish -File -Recurse)) {
    if ($file.Extension -in @(".wav", ".mp3", ".flac", ".ogg", ".opus", ".vst3")) {
        throw "Unexpected audio/plugin in package: $($file.Name)"
    }
}
$buildInfo = [ordered]@{
    version = $version
    source_commit = $commit
    runtime = "win-x64"
    sdk = (& $Dotnet --version).Trim()
    build_utc = [DateTime]::UtcNow.ToString("o")
    signed = $false
    release_status = "draft-pending-acceptance-and-licensing"
}
$buildInfo | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish "BUILD.json") -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = Join-Path $OutputDirectory "WaveBenchPro-$version-win-x64.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($publish, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
$digest = Get-Sha256 $archive
"$digest  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath (Join-Path $OutputDirectory "SHA256SUMS.txt") -Encoding ASCII
Write-Host "Package: $archive"
Write-Host "Source commit: $commit"
Write-Host "SHA256: $digest"
