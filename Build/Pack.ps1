param(
    [string]$MSBuildPath = '',
    [string]$NuGetPath = 'nuget',
    [string]$OutputPath = '',
    [switch]$NoBuild,
    [ValidateSet('Modbus','DNP3','BACnet','SNMP','MMS','IEC61850','MQTT','Sparkplug')][string]$LibrarySet = 'Modbus'
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$metadata = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PackageMetadata.props')
$projectUrl = [Security.SecurityElement]::Escape($metadata.Project.PropertyGroup.PackageProjectUrl)
$repositoryUrl = [Security.SecurityElement]::Escape($metadata.Project.PropertyGroup.RepositoryUrl)
$authors = [Security.SecurityElement]::Escape($metadata.Project.PropertyGroup.Authors)
$releaseNotes = [Security.SecurityElement]::Escape($metadata.Project.PropertyGroup.PackageReleaseNotes)
$catalog = Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw -Encoding UTF8
if (-not $OutputPath) { $OutputPath = Join-Path $PSScriptRoot ('..\bin\nuget\' + $LibrarySet.ToLowerInvariant()) }
$output = [IO.Path]::GetFullPath($OutputPath)
$version = '0.1.0-preview.1'
$assemblyInfo = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AssemblyInfo.cs') -Raw
if ($assemblyInfo -notmatch 'AssemblyVersion\("0\.1\.0\.0"\)') {
    throw 'Update the package version and smoke fixture when changing the assembly version.'
}
[xml]$projects = Get-Content -LiteralPath (Join-Path $root 'VagabondK.nF.proj')
$libraries = @($projects.Project.ItemGroup.ChildNodes | Where-Object { $_.LocalName -eq "${LibrarySet}Library" -or ($LibrarySet -eq 'IEC61850' -and $_.LocalName -eq 'MMSLibrary') })
foreach ($library in $libraries) {
    $name = [IO.Path]::GetFileNameWithoutExtension($library.Include)
    $packageFile = Join-Path $output "$name.$version.nupkg"
    if (Test-Path -LiteralPath $packageFile) { throw "Package already exists; choose a fresh output folder: $packageFile" }
}
if (-not $MSBuildPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Specify -MSBuildPath for Visual Studio MSBuild.' }
    $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
        Select-Object -First 1
    if (-not $MSBuildPath) { throw 'Visual Studio MSBuild was not found.' }
}
if (-not $NoBuild) {
    & $MSBuildPath (Join-Path $root 'VagabondK.nF.proj') /t:Rebuild /p:Configuration=Release /nr:false /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw 'Library build failed.' }
}
$null = New-Item -ItemType Directory -Path $output -Force
$specPath = Join-Path $output 'specs'
$null = New-Item -ItemType Directory -Path $specPath -Force
[xml]$manifest = Get-Content -LiteralPath (Join-Path $root 'packages.config')
$hashes = @()
foreach ($library in $libraries) {
    $projectPath = Join-Path $root $library.Include
    $name = [IO.Path]::GetFileNameWithoutExtension($projectPath)
    $packageFile = Join-Path $output "$name.$version.nupkg"
    if (Test-Path -LiteralPath $packageFile) { throw "Package already exists; choose a fresh output folder: $packageFile" }
    [xml]$project = Get-Content -LiteralPath $projectPath
    $references = @($project.SelectNodes("//*[local-name()='Reference']"))
    foreach ($import in $project.SelectNodes("//*[local-name()='Import']")) {
        if ($import.Project -like '*Library.props' -or $import.Project -like '*Network.props') {
            [xml]$props = Get-Content -LiteralPath (Join-Path (Split-Path $projectPath) $import.Project)
            $references += @($props.SelectNodes("//*[local-name()='Reference']"))
        }
    }
    $dependencies = @{}
    foreach ($reference in $references) {
        if (-not $reference.HintPath) { continue }
        $folder = ($reference.HintPath -replace '^\$\(PackagesPath\)', '').Split('\')[0]
        $package = @($manifest.packages.package | Where-Object { "$($_.id).$($_.version)" -eq $folder })
        if ($package.Count -ne 1) { throw "Unmapped package reference: $($reference.HintPath)" }
        $dependencies[$package[0].id] = $package[0].version
    }
    foreach ($reference in $project.SelectNodes("//*[local-name()='ProjectReference']")) {
        $dependencies[[IO.Path]::GetFileNameWithoutExtension($reference.Include)] = $version
    }
    $dependencyXml = @($dependencies.Keys | Sort-Object | ForEach-Object {
        '      <dependency id="{0}" version="[{1}]" />' -f $_, $dependencies[$_]
    }) -join "`n"
    $files = @()
    foreach ($extension in @('dll', 'pe', 'pdbx', 'xml')) {
        $source = Join-Path (Split-Path $projectPath) "bin\Release\$name.$extension"
        if (-not (Test-Path -LiteralPath $source)) { throw "Missing package asset: $source" }
        $files += '    <file src="{0}" target="lib" />' -f [Security.SecurityElement]::Escape($source)
    }
    $readme = Join-Path $specPath "readmes\$name\README.md"
    & (Join-Path $PSScriptRoot 'New-PackageReadme.ps1') -PackageId $name -Version $version -OutputPath $readme
    $entry = [regex]::Match($catalog, '(?m)^\| \[' + [regex]::Escape($name) + '\]\([^)]+\) \| [^|]+ \| ([^|]+) \|')
    if (-not $entry.Success) { throw "Missing package description: $name" }
    $description = [Security.SecurityElement]::Escape('.NET nanoFramework용 평가 패키지입니다. 주요 기능: ' + $entry.Groups[1].Value.Trim())
    $tags = 'nanoFramework embedded ' + $name.Replace('.', ' ')
    $files += '    <file src="{0}" target="docs" />' -f [Security.SecurityElement]::Escape($readme)
    $spec = @"
<?xml version="1.0" encoding="utf-8"?>
<package>
  <metadata>
    <id>$name</id>
    <version>$version</version>
    <authors>$authors</authors>
    <projectUrl>$projectUrl</projectUrl>
    <repository type="git" url="$repositoryUrl" />
    <license type="expression">MIT</license>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <description>$description</description>
    <icon>nugetIcon.png</icon>
    <readme>docs\README.md</readme>
    <tags>$tags</tags>
    <releaseNotes>$releaseNotes</releaseNotes>
    <dependencies>
$dependencyXml
    </dependencies>
  </metadata>
  <files>
    <file src="$([Security.SecurityElement]::Escape((Join-Path $root 'LICENSE')))" target="LICENSE" />
    <file src="$([Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'nugetIcon.png')))" target="" />
$($files -join "`n")
  </files>
</package>
"@
    $specFile = Join-Path $specPath "$name.nuspec"
    [IO.File]::WriteAllText($specFile, $spec, [Text.UTF8Encoding]::new($false))
    & $NuGetPath pack $specFile -OutputDirectory $output -NonInteractive -Verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "NuGet pack failed: $name" }
    $hash = Get-FileHash -LiteralPath $packageFile -Algorithm SHA256
    $hashes += [pscustomobject]@{ Package = [IO.Path]::GetFileName($packageFile); SHA256 = $hash.Hash }
}
$hashes | Export-Csv -LiteralPath (Join-Path $output 'sha256.csv') -NoTypeInformation
Write-Output "PACK PASS: $($hashes.Count) packages ($version), local only: $output"
