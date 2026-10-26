param(
    [Parameter(Mandatory = $true)]
    [string]$Plugin,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root "src/$Plugin/$Plugin.csproj"
if (-not (Test-Path $project)) { throw "There's no plugin project at src/$Plugin/$Plugin.csproj" }

$version = (dotnet msbuild $project -getProperty:Version).Trim()
dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Building $Plugin failed" }

$built = (Resolve-Path (Join-Path $root "src/$Plugin/bin/$Configuration/net10.0")).Path
if (-not (Test-Path (Join-Path $built 'manifest.json'))) { throw "$Plugin's build has no manifest.json" }

$artifacts = New-Item -ItemType Directory -Force (Join-Path $root 'artifacts')
$staging = Join-Path $artifacts.FullName "plugins/$Plugin"
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force $staging | Out-Null
Get-ChildItem $built -File -Recurse | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object {
    $relative = $_.FullName.Substring($built.Length).TrimStart('\', '/')
    $destination = Join-Path $staging $relative
    New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
    Copy-Item $_.FullName $destination
}

$zip = Join-Path $artifacts.FullName "$Plugin-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Done: $zip"
Write-Host "SHA-256: $hash"
