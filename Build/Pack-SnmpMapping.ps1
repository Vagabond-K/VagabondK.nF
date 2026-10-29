param(
    [string]$MSBuildPath = '',
    [string]$NuGetPath = 'nuget',
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputPath) { $OutputPath = Join-Path $root 'bin\nuget\snmp-mapping' }
$output = [IO.Path]::GetFullPath($OutputPath)
$name = 'VagabondK.nF.SNMP.Mapping'
$package = Join-Path $output "$name.0.1.0-preview.1.nupkg"
if (Test-Path -LiteralPath $package) { throw "Package already exists; choose a fresh output folder: $package" }
& (Join-Path $PSScriptRoot 'Pack.ps1') -MSBuildPath $MSBuildPath -NuGetPath $NuGetPath -OutputPath $output -LibrarySet SNMP
& dotnet pack (Join-Path $root "Source\SNMP\$name\$name.csproj") -c Release -o $output --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'SNMP mapping generator package failed.' }
if (-not (Test-Path -LiteralPath $package)) { throw 'Generator package was not produced.' }
Write-Output "SNMP MAPPING PACK PASS: 2 runtime packages and 1 build-only generator package in $output"
