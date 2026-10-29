#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($Path)
[xml]$projects = Get-Content -LiteralPath (Join-Path $root 'VagabondK.nF.proj')
$names = @($projects.Project.ItemGroup.ChildNodes |
    Where-Object { $_.LocalName -like '*Library' -or $_.LocalName -eq 'BuildTool' } |
    ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Include) } | Sort-Object -Unique)
$expected = @($names | ForEach-Object { "$_.$Version.nupkg" })
$packages = @(Get-ChildItem -LiteralPath (Join-Path $output 'packages') -File)
$inventory = @(Import-Csv -LiteralPath (Join-Path $output 'sha256.csv'))
if ($names.Count -ne 34 -or $packages.Count -ne $names.Count -or $inventory.Count -ne $names.Count -or
    @(Compare-Object $expected @($packages.Name)).Count -ne 0 -or
    @(Compare-Object $expected @($inventory.Package)).Count -ne 0) {
    throw 'Release artifact must contain exactly the expected 34 versioned packages and checksum rows.'
}
foreach ($name in $names) {
    $fileName = "$name.$Version.nupkg"
    $file = Join-Path $output "packages\$fileName"
    $row = @($inventory | Where-Object Package -CEQ $fileName)
    if ($row.Count -ne 1 -or $row[0].SHA256 -notmatch '^[0-9A-Fa-f]{64}$' -or
        $row[0].SHA256 -ne (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash) {
        throw "Release checksum mismatch: $fileName"
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($file)
    try {
        $spec = @($zip.Entries | Where-Object FullName -like '*.nuspec')
        if ($spec.Count -ne 1) { throw "Expected one NuGet manifest: $fileName" }
        $reader = [IO.StreamReader]::new($spec[0].Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($manifest.package.metadata.id -cne $name -or $manifest.package.metadata.version -cne $Version) {
            throw "Release package identity/version mismatch: $fileName"
        }
        if (-not $manifest.package.metadata.readme -or
            -not $zip.GetEntry($manifest.package.metadata.readme.Replace('\', '/'))) {
            throw "Release package README missing: $fileName"
        }
        foreach ($dependency in $manifest.SelectNodes("//*[local-name()='dependency']")) {
            if ($dependency.id.StartsWith('VagabondK.nF.') -and $dependency.version -cne "[$Version]") {
                throw "Release dependency version mismatch: $fileName -> $($dependency.id)"
            }
        }
    }
    finally { $zip.Dispose() }
}
Write-Output "RELEASE ARTIFACT PASS: $($names.Count) packages; version $Version; identities, dependencies, README and SHA256 verified."
