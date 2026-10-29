#Requires -Version 7.0
param(
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe',
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$peer = Join-Path $root 'bin\protocol-tools\iec61850-build\Release\VagabondKMmsPeer.exe'
if (-not (Test-Path -LiteralPath $peer)) { throw 'Run Build-Iec61850ValidationPeer.ps1 first.' }
$source = Join-Path $root 'bin\protocol-tools\libiec61850'
$revision = & git -C $source rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $revision -ne 'f0f797bf955da763353f7081c892a1ff6b55e5a1') { throw 'Unexpected independent peer revision.' }
& git -C $source diff --quiet $revision --
if ($LASTEXITCODE -ne 0) { throw 'Independent MMS dependency was modified.' }
$identity = & $peer --identity
if ($LASTEXITCODE -ne 0 -or $identity -ne 'libiec61850 v1.6.2 ORIGINAL f0f797bf955da763353f7081c892a1ff6b55e5a1') { throw 'Unexpected MMS peer identity.' }
if (-not $OutputPath) { $OutputPath = Join-Path $root ('bin\iec61850-tests-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output folder.' }
$null = New-Item -ItemType Directory -Path $output
Get-FileHash -LiteralPath $peer | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'peer-image.json')
foreach ($configuration in @('Debug','Release')) {
    & $MSBuildPath (Join-Path $root 'Source\MMS\VagabondK.nF.MMS.TCP\VagabondK.nF.MMS.TCP.nfproj') `
        /t:Rebuild "/p:Configuration=$configuration" /warnaserror /v:quiet /nologo *>&1 |
        Tee-Object -FilePath (Join-Path $output "build-$configuration.log")
    if ($LASTEXITCODE -ne 0) { throw "IEC61850 $configuration MCU build failed." }
    & $MSBuildPath (Join-Path $root 'Source\IEC61850\VagabondK.nF.IEC61850.Server\VagabondK.nF.IEC61850.Server.nfproj') `
        /t:Rebuild "/p:Configuration=$configuration" /warnaserror /v:quiet /nologo *>&1 |
        Tee-Object -FilePath (Join-Path $output "build-server-$configuration.log")
    if ($LASTEXITCODE -ne 0) { throw "IEC61850 Server $configuration MCU build failed." }
    & $MSBuildPath (Join-Path $root 'Source\IEC61850\VagabondK.nF.IEC61850.Client\VagabondK.nF.IEC61850.Client.nfproj') `
        /t:Rebuild "/p:Configuration=$configuration" /warnaserror /v:quiet /nologo *>&1 |
        Tee-Object -FilePath (Join-Path $output "build-client-$configuration.log")
    if ($LASTEXITCODE -ne 0) { throw "IEC61850 Client $configuration MCU build failed." }
    $dependencies = [ordered]@{
        'VagabondK.nF.MMS' = @()
        'VagabondK.nF.MMS.Client' = @('VagabondK.nF.MMS')
        'VagabondK.nF.MMS.Server' = @('VagabondK.nF.MMS')
        'VagabondK.nF.MMS.TCP' = @('VagabondK.nF.MMS')
        'VagabondK.nF.IEC61850' = @('VagabondK.nF.MMS')
        'VagabondK.nF.IEC61850.Client' = @('VagabondK.nF.MMS','VagabondK.nF.MMS.Client','VagabondK.nF.IEC61850')
        'VagabondK.nF.IEC61850.Server' = @('VagabondK.nF.MMS','VagabondK.nF.MMS.Server','VagabondK.nF.IEC61850')
    }
    $references = [ordered]@{}
    foreach ($name in $dependencies.Keys) {
        $protocol = $name.Split('.')[2]
        $stream = [IO.File]::OpenRead((Join-Path $root "Source\$protocol\$name\bin\$configuration\$name.dll"))
        try {
            $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
            try {
                $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
                $actual = @($metadata.AssemblyReferences | ForEach-Object {
                    $reference = $metadata.GetAssemblyReference($_)
                    $metadata.GetString($reference.Name)
                } | Where-Object { $_ -like 'VagabondK.nF.*' } | Sort-Object)
            }
            finally { $pe.Dispose() }
        }
        finally { $stream.Dispose() }
        $expected = @($dependencies[$name] | Sort-Object)
        if (($actual -join ';') -ne ($expected -join ';')) {
            throw "Unexpected $name assembly references: $($actual -join ', ')"
        }
        $references[$name] = $actual
    }
    $references | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output "references-$configuration.json")
    & dotnet run -c $configuration --project (Join-Path $root 'Tests\IEC61850\VagabondK.nF.IEC61850.Tests\VagabondK.nF.IEC61850.Tests.csproj') `
        -- --interop $peer *>&1 | Tee-Object -FilePath (Join-Path $output "tests-$configuration.log")
    if ($LASTEXITCODE -ne 0) { throw "IEC61850 $configuration PC/interop tests failed." }
    & dotnet run -c $configuration --project (Join-Path $root 'Tests\IEC61850\VagabondK.nF.IEC61850.Tests\VagabondK.nF.IEC61850.Tests.csproj') `
        -- --server-perf *>&1 | Tee-Object -FilePath (Join-Path $output "server-perf-$configuration.log")
    if ($LASTEXITCODE -ne 0) { throw "IEC61850 Server $configuration allocation regression failed." }
    & dotnet run -c $configuration --project (Join-Path $root 'Tests\MMS\VagabondK.nF.MMS.Tests\VagabondK.nF.MMS.Tests.csproj') `
        -- $peer *>&1 | Tee-Object -FilePath (Join-Path $output "mms-tests-$configuration.log")
    if ($LASTEXITCODE -ne 0) { throw "MMS $configuration standalone tests failed." }
}
$identity | Set-Content -LiteralPath (Join-Path $output 'completed.txt')
Write-Output "IEC61850 PC INTEROP PASS: $output"
