param(
    [string]$MSBuildPath = '',
    [string]$NuGetPath = 'nuget',
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputPath) { $OutputPath = Join-Path $root 'bin\nuget\bacnet-mapping' }
$output = [IO.Path]::GetFullPath($OutputPath)
$name = 'VagabondK.nF.BACnet.Mapping'
$package = Join-Path $output "$name.0.1.0-preview.1.nupkg"
if (Test-Path -LiteralPath $package) { throw "Package already exists; choose a fresh output folder: $package" }
& (Join-Path $PSScriptRoot 'Pack.ps1') -MSBuildPath $MSBuildPath -NuGetPath $NuGetPath -OutputPath $output -LibrarySet BACnet
& dotnet pack (Join-Path $root "Source\BACnet\$name\$name.csproj") -c Release -o $output --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'BACnet mapping generator package failed.' }
if (-not (Test-Path -LiteralPath $package)) { throw 'Generator package was not produced.' }
Write-Output "BACNET MAPPING PACK PASS: 2 runtime packages and 1 build-only generator package in $output"
