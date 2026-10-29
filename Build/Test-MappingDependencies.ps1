param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$FeedPath,
    [string]$NuGetPath = 'nuget'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$feed = [IO.Path]::GetFullPath($FeedPath)
$version = '0.1.0-preview.1'
$runtime = $Name -replace '\.Mapping$', ''
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $feed "$Name.$version.nupkg"))
try {
    $reader = [IO.StreamReader]::new($zip.GetEntry("$Name.nuspec").Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $dependencies = @($manifest.SelectNodes("//*[local-name()='dependency']"))
    if ($dependencies.Count -ne 1 -or $dependencies[0].id -cne $runtime -or
        $dependencies[0].version -ne "[$version]") {
        throw "Expected exactly one pinned runtime dependency: $Name -> $runtime [$version]"
    }
}
finally { $zip.Dispose() }
$packages = Join-Path $root ('bin\mapping-dependency-restore-' + [Guid]::NewGuid().ToString('N'))
& $NuGetPath install $Name -Version $version -Source ($feed + ';' + (Join-Path $root 'packages')) `
    -OutputDirectory $packages -NonInteractive -NoHttpCache -DirectDownload -Verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Mapping-only install failed: $Name" }
$expected = @($Name, $runtime)
if ($runtime -eq 'VagabondK.nF.IEC61850') { $expected += 'VagabondK.nF.MMS' }
$actual = @(Get-ChildItem -LiteralPath $packages -Directory |
    Where-Object Name -like 'VagabondK.nF.*' | ForEach-Object { $_.Name -replace '\.\d+\.\d+\.\d+.*$', '' })
if (@(Compare-Object $expected $actual).Count -ne 0 -or
    @(Get-ChildItem -LiteralPath $packages -Directory | Where-Object Name -like '*CodeAnalysis*').Count) {
    throw "Mapping-only install restored missing/unselected runtime or Roslyn packages: $Name"
}
Write-Output "MAPPING DEPENDENCY PASS: installing only $Name automatically restored $runtime; no roles/transports/Roslyn."
