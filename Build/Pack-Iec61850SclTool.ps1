#Requires -Version 7.0
[CmdletBinding()]
param([string]$OutputPath = '')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputPath) { $OutputPath=Join-Path $root ('bin\nuget\iec61850-scl-'+(Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output=[IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output folder.' }
$null=New-Item -ItemType Directory -Path $output
$project=Join-Path $root 'Source\IEC61850\VagabondK.nF.IEC61850.SCL\VagabondK.nF.IEC61850.SCL.csproj'
& dotnet pack $project -c Release -o $output --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'SCL tool pack failed.' }
$config=Join-Path $output 'nuget.config'
$escaped=[Security.SecurityElement]::Escape($output)
[IO.File]::WriteAllText($config,"<configuration><packageSources><clear/><add key=`"local`" value=`"$escaped`"/></packageSources></configuration>")
$tools=Join-Path $output 'installed'
& dotnet tool install VagabondK.nF.IEC61850.SCL --version 0.1.0-preview.1 --tool-path $tools --configfile $config
if ($LASTEXITCODE -ne 0) { throw 'SCL tool offline install failed.' }
$generated=Join-Path $output 'GeneratedIec61850Model.cs'
& (Join-Path $tools 'vk-nf-iec61850-scl.exe') (Join-Path $root 'Tests\IEC61850\VagabondK.nF.IEC61850.Tests\Fixtures\Data.scd') $generated
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $generated)) { throw 'Installed SCL tool generation failed.' }
Get-ChildItem -LiteralPath $output -Filter '*.nupkg' -File | Get-FileHash |
    Select-Object @{Name='Package';Expression={[IO.Path]::GetFileName($_.Path)}},Hash |
    Export-Csv -LiteralPath (Join-Path $output 'sha256.csv') -NoTypeInformation
Write-Output "SCL TOOL PACKAGE PASS: offline install/generation, .NET 10 PC only, local only: $output"
