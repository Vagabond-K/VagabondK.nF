param(
    [ValidateSet('MMS', 'IEC61850')][string]$LibrarySet = 'MMS',
    [string]$MSBuildPath = '',
    [string]$NuGetPath = 'nuget',
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputPath) { $OutputPath = Join-Path $root ('bin\nuget\' + $LibrarySet.ToLowerInvariant() + '-mapping') }
$output = [IO.Path]::GetFullPath($OutputPath)
$protocols = @('MMS')
if ($LibrarySet -eq 'IEC61850') { $protocols += 'IEC61850' }
foreach ($protocol in $protocols) {
    $name = "VagabondK.nF.$protocol.Mapping"
    if (Test-Path -LiteralPath (Join-Path $output "$name.0.1.0-preview.1.nupkg")) { throw 'Choose a fresh output folder.' }
}
& (Join-Path $PSScriptRoot 'Pack.ps1') -LibrarySet $LibrarySet -MSBuildPath $MSBuildPath -NuGetPath $NuGetPath -OutputPath $output
foreach ($protocol in $protocols) {
    $name = "VagabondK.nF.$protocol.Mapping"
    & dotnet pack (Join-Path $root "Source\$protocol\$name\$name.csproj") -c Release -o $output --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "$protocol mapping generator pack failed." }
}
Write-Output "$LibrarySet MAPPING PACK PASS: $output"
