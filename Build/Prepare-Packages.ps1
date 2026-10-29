#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputPath = '',
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe',
    [string]$NuGetPath = 'nuget'
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputPath) {
    $OutputPath = Join-Path $root ('bin\release-candidate-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output folder.' }
$null = New-Item -ItemType Directory -Path $output
$logs = Join-Path $output 'logs'
$null = New-Item -ItemType Directory -Path $logs
$env:MSBUILDDISABLENODEREUSE = '1'
$version = '0.1.0-preview.1'
$sets = @('Modbus', 'DNP3', 'BACnet', 'SNMP', 'MMS', 'IEC61850', 'MQTT', 'Sparkplug')

& (Join-Path $PSScriptRoot 'Test-PackageReadmes.ps1') -OutputPath (Join-Path $output 'readmes') `
    *> (Join-Path $logs 'readmes.log')
& $MSBuildPath (Join-Path $root 'VagabondK.nF.proj') /t:Rebuild /p:Configuration=Release /nr:false /warnaserror /v:quiet /nologo `
    *> (Join-Path $logs 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Release build failed; see logs/build.log.' }
foreach ($set in $sets) {
    $feed = Join-Path $output $set
    & (Join-Path $PSScriptRoot 'Pack.ps1') -LibrarySet $set -OutputPath $feed `
        -MSBuildPath $MSBuildPath -NuGetPath $NuGetPath -NoBuild *> (Join-Path $logs "pack-$set.log")
    $group = if ($set -eq 'Sparkplug') { 'MQTT' } else { $set }
    $mapping = if ($set -eq 'Sparkplug') { 'VagabondK.nF.MQTT.Sparkplug.Mapping' } else { "VagabondK.nF.$set.Mapping" }
    & dotnet pack (Join-Path $root "Source\$group\$mapping\$mapping.csproj") -c Release -o $feed --nologo -v quiet `
        *> (Join-Path $logs "pack-$set-mapping.log")
    if ($LASTEXITCODE -ne 0) { throw "Mapping pack failed; see logs: $set" }
    Write-Output "PACKED: $set"
}
$scl = Join-Path $output 'SCL'
& (Join-Path $PSScriptRoot 'Pack-Iec61850SclTool.ps1') -OutputPath $scl *> (Join-Path $logs 'scl-install.log')

foreach ($set in $sets) {
    & (Join-Path $PSScriptRoot 'Test-Packages.ps1') -LibrarySet $set -FeedPath (Join-Path $output $set) `
        -MSBuildPath $MSBuildPath -NuGetPath $NuGetPath *> (Join-Path $logs "runtime-$set.log")
    Write-Output "RUNTIME VERIFIED: $set"
}
# MMS·IEC는 양 역할과 두 매핑 패키지를 함께 확인하는 소비 프로젝트를 공유합니다.
Copy-Item -LiteralPath (Join-Path $output "MMS\VagabondK.nF.MMS.Mapping.$version.nupkg") -Destination (Join-Path $output 'IEC61850')
foreach ($set in @('Modbus', 'DNP3', 'BACnet', 'SNMP', 'IEC61850', 'MQTT', 'Sparkplug')) {
    $script = switch ($set) {
        'IEC61850' { 'Test-MmsMapping.ps1' }
        { $_ -in @('MQTT', 'Sparkplug') } { 'Test-MqttMapping.ps1' }
        default { "Test-$($set.Substring(0, 1))$($set.Substring(1).ToLowerInvariant())Mapping.ps1" }
    }
    $parameters = @{ FeedPath = Join-Path $output $set; MSBuildPath = $MSBuildPath; NuGetPath = $NuGetPath }
    if ($set -in @('MQTT', 'Sparkplug')) { $parameters.LibrarySet = $set }
    & (Join-Path $PSScriptRoot $script) @parameters *> (Join-Path $logs "mapping-$set.log")
    Write-Output "MAPPING VERIFIED: $set"
}

$packages = Join-Path $output 'packages'
$null = New-Item -ItemType Directory -Path $packages
foreach ($set in @('Modbus', 'DNP3', 'BACnet', 'SNMP', 'IEC61850', 'MQTT', 'Sparkplug', 'SCL')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $output $set) -File -Filter '*.nupkg') {
        Copy-Item -LiteralPath $file.FullName -Destination $packages
    }
}
$files = @(Get-ChildItem -LiteralPath $packages -File -Filter '*.nupkg')
if ($files.Count -ne 34) { throw "Expected 34 packages, got $($files.Count)." }
[xml]$settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PackageMetadata.props')
$iconHash = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'nugetIcon.png')).Hash
$inventory = foreach ($file in $files) {
    $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $entry = @($zip.Entries | Where-Object FullName -like '*.nuspec')
        $reader = [IO.StreamReader]::new($entry[0].Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $manifest.package.metadata
        foreach ($field in @('id', 'version', 'description', 'tags', 'releaseNotes', 'readme', 'icon')) {
            if ([string]::IsNullOrWhiteSpace($metadata.$field)) { throw "Missing $field in $($file.Name)." }
        }
        if ($metadata.authors -cne 'Vagabond K' -or $metadata.version -ne $version -or
            $metadata.projectUrl -ne $settings.Project.PropertyGroup.PackageProjectUrl -or
            $metadata.repository.url -ne $settings.Project.PropertyGroup.RepositoryUrl -or
            $metadata.repository.type -ne 'git' -or $metadata.license.InnerText -ne 'MIT') {
            throw "Incorrect package metadata: $($file.Name)"
        }
        $stream = $zip.GetEntry($metadata.icon).Open()
        try { $actualIcon = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($actualIcon -ne $iconHash) { throw "Icon mismatch: $($file.Name)" }
        $reader = [IO.StreamReader]::new($zip.GetEntry($metadata.readme.Replace('\', '/')).Open())
        try { $readme = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if (-not $readme.StartsWith("# $($metadata.id)") -or
            -not $readme.Contains('## 설치') -or -not $readme.Contains('/blob/HEAD/Source/')) {
            throw "Invalid package README: $($file.Name)"
        }
        $expectedReadme = Get-Content -LiteralPath (Join-Path $output "readmes\$($metadata.id)\README.md") -Raw -Encoding UTF8
        if ($readme -cne $expectedReadme) { throw "Stale package README: $($file.Name)" }
        [pscustomobject]@{
            Package = $file.Name
            SHA256 = (Get-FileHash -LiteralPath $file.FullName).Hash
            Description = $metadata.description
        }
    }
    finally { $zip.Dispose() }
}
$inventory | Export-Csv -LiteralPath (Join-Path $output 'sha256.csv') -NoTypeInformation -Encoding utf8
Write-Output "LOCAL RELEASE CANDIDATE PASS: 34 packages; runtime, mapping and SCL installation verified. No upload or device changes: $packages"
