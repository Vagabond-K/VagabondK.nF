<#
.SSIB
  VagabondK.nF.WebUi (build-tools) NuGet packaging
  - Output: bin\nuget\webui\VagabondK.nF.WebUi.<ver>.nupkg
  - Contents: build\VagabondK.nF.WebUi.targets + Assets + Components + Pages + tools\{net472,netstandard2.0}\Tasks dll
  - Consumers (nanoGate, etc.) build WebUi resources with this package without needing the repo.

.PARAMETER PackageVersion
  Package version (default: 0.1.0-preview.1)
.PARAMETER OutputPath
  Output folder for .nupkg (default: bin\nuget\webui)
.PARAMETER NuGetPath
  Path to nuget.exe (default: PATH nuget)

.NOTES
  - No lib\/ - build tools only, no compile references.
  - Re-run fails if an existing package has the same version. Bump the version to deploy.
  - After packaging, update the targets import path in nanoGate's Management.targets.

.EXAMPLE
  .\Pack-WebUi.ps1
  .\Pack-WebUi.ps1 -PackageVersion 0.1.0-preview.2
#>
param(
    [string]$PackageVersion = '0.1.0-preview.1',
    [string]$OutputPath = '',
    [string]$NuGetPath = 'nuget'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) { $OutputPath = Join-Path $root 'bin\nuget\webui' }

$projDir    = Join-Path $root 'Source\Web\VagabondK.nF.WebUi'
$tasksProj  = Join-Path $root 'Source\Web\VagabondK.nF.WebUi.Tasks\VagabondK.nF.WebUi.Tasks.csproj'

if (-not (Test-Path (Join-Path $OutputPath "VagabondK.nF.WebUi.$PackageVersion.nupkg"))) {
    # ---- 1. Build the Tasks dll (both TFMs) ----
    dotnet build $tasksProj -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "VagabondK.nF.WebUi.Tasks build failed." }

    # ---- 2. Stage ----
    $stage = Join-Path $root 'bin\pack\webui'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    foreach ($d in 'build\Assets', 'build\Components', 'build\Pages', 'build\tools\net472', 'build\tools\netstandard2.0') {
        New-Item -ItemType Directory -Force -Path (Join-Path $stage $d) | Out-Null
    }

    Copy-Item (Join-Path $projDir 'VagabondK.nF.WebUi.targets') (Join-Path $stage 'build') -Force
    Copy-Item (Join-Path $projDir 'Assets\*') (Join-Path $stage 'build\Assets') -Recurse -Force
    # Components: *.nf.* triplet + texts*.resx dictionary (README/USAGE documentation is not distributed)
    Get-ChildItem (Join-Path $projDir 'Components\*') -Include '*.nf.*', 'texts*.resx' -File |
        Copy-Item -Destination (Join-Path $stage 'build\Components') -Force
    # Pages: library-provided routed pages (login.nf.* triplet; documentation excluded)
    Get-ChildItem (Join-Path $projDir 'Pages\*') -Include '*.nf.*' -File |
        Copy-Item -Destination (Join-Path $stage 'build\Pages') -Force
    foreach ($tfm in 'net472', 'netstandard2.0') {
        $dll = Join-Path $root "Source\Web\VagabondK.nF.WebUi.Tasks\bin\Release\$tfm\VagabondK.nF.WebUi.Tasks.dll"
        if (-not (Test-Path $dll)) { throw "Tasks build output not found: $dll" }
        Copy-Item $dll (Join-Path $stage "build\tools\$tfm") -Force
    }

    # ---- 3. nuspec ----
    $metaPath = Join-Path $root 'Build\PackageMetadata.props'
    $meta = @{}
    if (Test-Path $metaPath) {
        [xml]$metaXml = Get-Content $metaPath -Raw
        foreach ($n in $metaXml.Project.PropertyGroup.MSBuild.PackagingMetadata) {
            $meta[$n.Attributes['key'].Value] = $n.Attributes['value'].Value
        }
    }
    if (-not $meta['author']) { $meta['author'] = 'KuJune MOON' }
    if (-not $meta['url'])    { $meta['url'] = 'https://github.com/vagabondking/VagabondK.nF' }

    $description = 'Web UI resource generator (build-time MSBuild task): standard components (*.nf.html/css/js), library pages (Pages\login.nf.*), common i18n dictionaries (texts*.resx), core.js/i18n.js/theme.css assets, and the gzip resource generation task dll. Import build\VagabondK.nF.WebUi.targets to embed the admin page into the device image without needing the repository source.'

    @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>VagabondK.nF.WebUi</id>
    <version>$PackageVersion</version>
    <title>VagabondK.nF.WebUi</title>
    <authors>$($meta['author'])</authors>
    <license type="expression">MIT</license>
    <projectUrl>$($meta['url'])</projectUrl>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <description>$description</description>
    <tags>nanoFramework webui components i18n build-tools</tags>
  </metadata>
  <files>
    <file src="$stage\build\**" target="build" />
  </files>
</package>
"@ | Out-File (Join-Path $stage 'package.nuspec') -Encoding UTF8

    # ---- 4. pack ----
    New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
    & $NuGetPath pack (Join-Path $stage 'package.nuspec') -OutputDirectory $OutputPath -NonInteractive
    if ($LASTEXITCODE -ne 0) { throw "nuget pack failed (exit $LASTEXITCODE)." }
}

# ---- 5. Result ----
$nupkg = Join-Path $OutputPath "VagabondK.nF.WebUi.$PackageVersion.nupkg"
if (Test-Path $nupkg) {
    Write-Host "  OK : $nupkg" -ForegroundColor Green
    Write-Host "  nanoGate: add 'nuget install VagabondK.nF.WebUi -Version $PackageVersion -Source $OutputPath -OutputDirectory packages' to the packages feed." -ForegroundColor DarkGray
} else {
    Write-Warning "  ?? Package not found: $nupkg"
}
