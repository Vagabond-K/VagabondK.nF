<#
.SSIB
  VagabondK.nF.WebUi (build-tools) NuGet packaging
  - Output: bin\nuget\webui\VagabondK.nF.WebUi.<ver>.nupkg
  - Contents: build\VagabondK.nF.WebUi.targets + Assets + Components + Pages + tools\{net472,netstandard2.0}\Tasks dll
  - Consumers (nanoGate, etc.) build WebUi resources with this package without needing the repo.

.PARAMETER PackageVersion
  Package version (lockstep with the release, default below)
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
    [string]$PackageVersion = '0.1.0-preview.8',
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

    # ---- 3. Generate README/icon and nuspec ----
    & (Join-Path $PSScriptRoot 'New-PackageReadme.ps1') -PackageId VagabondK.nF.WebUi -OutputPath (Join-Path $stage 'docs\README.md') -Version $PackageVersion
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'nugetIcon.png') -Destination (Join-Path $stage 'nugetIcon.png') -Force
    [xml]$props = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PackageMetadata.props') -Encoding UTF8
    $pg = $props.Project.PropertyGroup
    $authors = [Security.SecurityElement]::Escape([string]$pg.Authors)
    $projectUrl = [Security.SecurityElement]::Escape([string]$pg.PackageProjectUrl)
    $repositoryUrl = [Security.SecurityElement]::Escape([string]$pg.RepositoryUrl)
    $releaseNotes = [Security.SecurityElement]::Escape([string]$pg.PackageReleaseNotes)

    $catalog = Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw -Encoding UTF8
    $entry = [regex]::Match($catalog, '(?m)^\| \[VagabondK\.nF\.WebUi\]\([^)]+\) \| ([^|]+) \| ([^|]+) \|')
    if (-not $entry.Success) { throw 'Missing package description: VagabondK.nF.WebUi' }
    $description = [Security.SecurityElement]::Escape('.NET nanoFramework용 평가 패키지입니다. 주요 기능: ' + $entry.Groups[2].Value.Trim())

    @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>VagabondK.nF.WebUi</id>
    <version>$PackageVersion</version>
    <title>VagabondK.nF.WebUi</title>
    <authors>$authors</authors>
    <license type="expression">MIT</license>
    <projectUrl>$projectUrl</projectUrl>
    <repository type="git" url="$repositoryUrl" />
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <description>$description</description>
    <tags>nanoFramework webui components i18n build-tools</tags>
    <releaseNotes>$releaseNotes</releaseNotes>
    <icon>nugetIcon.png</icon>
    <readme>docs\README.md</readme>
  </metadata>
  <files>
    <file src="$stage\build\**" target="build" />
    <file src="$stage\docs\README.md" target="docs" />
    <file src="$stage\nugetIcon.png" target="" />
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
    Write-Host "  consumers: 'nuget install VagabondK.nF.WebUi -Version $PackageVersion' (nuget.org after release)." -ForegroundColor DarkGray
} else {
    Write-Warning "  ?? Package not found: $nupkg"
}
