param(
    [ValidateSet('MQTT','Sparkplug')][string]$LibrarySet = 'MQTT',
    [string]$MSBuildPath = '',
    [string]$NuGetPath = 'nuget',
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$name = if ($LibrarySet -eq 'Sparkplug') { 'VagabondK.nF.MQTT.Sparkplug.Mapping' } else { 'VagabondK.nF.MQTT.Mapping' }
if (-not $OutputPath) { $OutputPath = Join-Path $root ('bin\nuget\' + $LibrarySet.ToLowerInvariant() + '-mapping') }
$output = [IO.Path]::GetFullPath($OutputPath)
$package = Join-Path $output "$name.0.1.0-preview.1.nupkg"
if (Test-Path -LiteralPath $package) { throw "Package already exists; choose a fresh output folder: $package" }
& (Join-Path $PSScriptRoot 'Pack.ps1') -MSBuildPath $MSBuildPath -NuGetPath $NuGetPath -OutputPath $output -LibrarySet $LibrarySet
& dotnet pack (Join-Path $root "Source\MQTT\$name\$name.csproj") -c Release -o $output --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "$LibrarySet mapping generator package failed." }
if (-not (Test-Path -LiteralPath $package)) { throw 'Generator package was not produced.' }
Write-Output "MQTT MAPPING PACK PASS: $LibrarySet runtime and build-only generator in $output"
