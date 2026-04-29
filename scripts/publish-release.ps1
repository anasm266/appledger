param(
    [string] $Runtime = "win-x64",
    [string] $Configuration = "Release",
    [string] $Version = "dev",
    [string] $OutputRoot = "artifacts\release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "src\AppLedger.Cli\AppLedger.Cli.csproj"
$releaseVersion = $Version.Trim()
if ([string]::IsNullOrWhiteSpace($releaseVersion) -or $releaseVersion -eq "dev") {
    $releaseVersion = "0.0.0-dev"
}

if ($releaseVersion.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
    $releaseVersion = $releaseVersion.Substring(1)
}

$releaseRoot = Join-Path $repoRoot $OutputRoot
$publishDir = Join-Path $releaseRoot "appledger-$Runtime"
$zipPath = Join-Path $releaseRoot "appledger-$Runtime.zip"
$shaPath = "$zipPath.sha256"

if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $publishDir `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    -p:PublishReadyToRun=false `
    -p:Version=$releaseVersion

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $publishDir "appledger.exe"
if (-not (Test-Path $exe)) {
    throw "Expected single-file executable was not produced: $exe"
}

Get-ChildItem -LiteralPath $publishDir -File |
    Where-Object { $_.Name -ne "appledger.exe" } |
    Remove-Item -Force

$readme = Join-Path $repoRoot "README.md"
if (Test-Path $readme) {
    Copy-Item -LiteralPath $readme -Destination (Join-Path $publishDir "README.md") -Force
}

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath
"$($hash.Hash.ToLowerInvariant())  $(Split-Path -Leaf $zipPath)" | Set-Content -LiteralPath $shaPath -Encoding ascii

Write-Host "Release package:"
Write-Host "  $zipPath"
Write-Host "  $shaPath"
Write-Host "Executable:"
Write-Host "  $exe"
