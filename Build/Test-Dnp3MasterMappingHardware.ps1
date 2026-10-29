#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][switch]$AllowDeviceChanges,
    [ValidateSet(7,8,32,128)][int[]]$Points = @(7,8,32,128),
    [string]$OutputPath = '',
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
)
$ErrorActionPreference = 'Stop'
if (-not $AllowDeviceChanges) { throw 'Use only the isolated COM3 evaluation board.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$restore = Join-Path $root 'bin\hardware\restore-Modbus.SerialServer.bin'
if ((Get-FileHash -LiteralPath $restore).Hash -ne '66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7') { throw 'Restore checksum mismatch.' }
if ([IO.Ports.SerialPort]::GetPortNames() -notcontains 'COM3') { throw 'COM3 unavailable.' }
$native = Join-Path $root 'bin\protocol-tools\master-build\Release\VagabondKDnp3Outstation.exe'
if ((& $native --decoder-identity) -ne 'DECODER ORIGINAL' -or $LASTEXITCODE -ne 0) { throw 'Unmodified OpenDNP3 Outstation required.' }
if (-not $OutputPath) { $OutputPath = Join-Path $root ('bin\dnp3-master-mapping-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output folder.' }
$null = New-Item -ItemType Directory -Path $output
& dotnet run -c Release --project (Join-Path $root 'Tests\DNP3\VagabondK.nF.DNP3.Mapping.Tests\VagabondK.nF.DNP3.Mapping.Tests.csproj') *>&1 |
    Tee-Object -FilePath (Join-Path $output 'pc-tests.log')
if ($LASTEXITCODE -ne 0) { throw 'PC mapping regression failed.' }
& $MSBuildPath (Join-Path $root 'Samples\DNP3\VagabondK.nF.DNP3.MasterMapping\VagabondK.nF.DNP3.MasterMapping.nfproj') /t:Rebuild /p:Configuration=Release /warnaserror /v:quiet /nologo
if ($LASTEXITCODE -ne 0) { throw 'Production Master sample build failed.' }
$escapedRoot = [Security.SecurityElement]::Escape($root)
$sizes = @()
foreach ($count in $Points) {
    $folder = Join-Path $output "$count"
    $null = New-Item -ItemType Directory -Path $folder
    $defines = 'DNP3_MASTER_MAPPING_TEST'
    if ($count -ne 7) {
        $defines += ';DNP3_MASTER_MAPPING_SCALING'
        $source = [Text.StringBuilder]::new()
        $null = $source.AppendLine('using System; using VagabondK.nF.DNP3; using VagabondK.nF.DNP3.Mapping; [Dnp3MasterMap] internal partial class ScalingModel {')
        $null = $source.AppendLine("public const int Count = $count; public int Records, Fragments; public bool Replay;")
        $null = $source.AppendLine('public Dnp3Measurement[][] Captured; public Dnp3ResponseInfo[] Responses;')
        $null = $source.AppendLine("public byte[] Quality = new byte[$([int][math]::Floor($count / 2))]; public long[] Times = new long[$count];")
        $types = @('Binary','DoubleBitBinary','Counter','FrozenCounter','Analog','BinaryOutputStatus','AnalogOutputStatus')
        for ($i = 0; $i -lt $count; $i++) {
            $kind = $i % 7; $index = [int][math]::Floor($i / 7)
            $type = if ($kind -in @(0,5)) { 'bool' } elseif ($kind -eq 1) { 'byte' } elseif ($kind -in @(2,3)) { 'uint' } else { 'double' }
            $flags = if ($i % 2) { "public byte F$i { set { Quality[$([int][math]::Floor($i / 2))] = value; } }" } else { "public byte F$i;" }
            $null = $source.AppendLine("[Dnp3Point(Dnp3PointType.$($types[$kind]),$index,Flags=nameof(F$i),Timestamp=nameof(T$i))] public $type P$i; $flags public long T$i { set { Times[$i] = value; } }")
        }
        $null = $source.AppendLine('[Dnp3Received] private void Received(Dnp3Measurement[] measurements,Dnp3ResponseInfo response) { Records += measurements.Length; if (!Replay && measurements.Length > 0) { if (Fragments == Captured.Length) throw new InvalidOperationException("Fragment storage full."); Captured[Fragments] = measurements; Responses[Fragments++] = response; } }')
        $null = $source.AppendLine('private string Source(Dnp3PointType type, ushort index) { for (int f = 0; f < Fragments; f++) for (int i = 0; i < Captured[f].Length; i++) { var item = Captured[f][i]; if (item.Type == type && item.Index == index) return " source raw=" + item.Value.Raw + " flags=" + item.Value.Flags + " time=" + item.Value.Timestamp; } return " source missing"; }')
        $null = $source.AppendLine('public bool Verify() { string failure = null;')
        for ($i = 0; $i -lt $count; $i++) {
            $kind = $i % 7; $index = [int][math]::Floor($i / 7)
            $value = if ($kind -in @(0,5)) { if ($index % 2) { 'true' } else { 'false' } } elseif ($kind -eq 1) { (1 + $index % 2).ToString() } elseif ($kind -in @(2,3)) { (1000 + $index).ToString() + 'U' } else { ($index.ToString() + $(if ($kind -eq 4) { '.25D' } else { '.5D' })) }
            $mask = if ($kind -in @(0,5)) { 127 } elseif ($kind -eq 1) { 63 } else { 255 }
            $flags = if ($i % 2) { "Quality[$([int][math]::Floor($i / 2))]" } else { "F$i" }
            $null = $source.AppendLine("if (P$i != $value || ($flags & $mask) != 1 || Times[$i] != 0) { if (failure == null) failure = `"Point $i`: value=`" + P$i + `" flags=`" + $flags + `" time=`" + Times[$i] + Source(Dnp3PointType.$($types[$kind]), $index); else failure += `"; $i`"; }")
        }
        $null = $source.AppendLine('if (failure != null) throw new InvalidOperationException(failure); return true; } }')
        [IO.File]::WriteAllText((Join-Path $folder 'ScalingModel.cs'), $source.ToString())
    }
    $project = @"
<Project ToolsVersion="Current" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup><ProjectGuid>{310E1601-9B23-4A51-B344-100000000115}</ProjectGuid></PropertyGroup>
  <Import Project="$escapedRoot\Build\Library.props" />
  <Import Project="$escapedRoot\Build\Network.props" />
  <PropertyGroup><OutputType>Exe</OutputType><DefineConstants>`$(DefineConstants);$defines</DefineConstants></PropertyGroup>
  <ItemGroup>
    <Reference Include="mscorlib"><HintPath>`$(PackagesPath)nanoFramework.CoreLibrary.1.17.11\lib\mscorlib.dll</HintPath><Private>true</Private></Reference>
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3.Mapping\VagabondK.nF.DNP3.Mapping.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true" />
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3\VagabondK.nF.DNP3.nfproj" />
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3.Master\VagabondK.nF.DNP3.Master.nfproj" />
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3.TCP\VagabondK.nF.DNP3.TCP.nfproj" />
    <Compile Include="$escapedRoot\Samples\DNP3\VagabondK.nF.DNP3.MasterMapping\Program.cs" />
    <Compile Include="$escapedRoot\Samples\DNP3\VagabondK.nF.DNP3.MasterMapping\ReceivedData.cs" />
    <Compile Include="$escapedRoot\Tests\DNP3\VagabondK.nF.DNP3.Mapping.Hardware\MasterValidation.cs" />
    <Compile Include="$escapedRoot\Tests\DNP3\VagabondK.nF.DNP3.Mapping.Hardware\MasterHardware.cs" />
  </ItemGroup>
  <Import Project="$escapedRoot\Build\Library.targets" />
</Project>
"@
    $name = "Dnp3MasterMapping$count"
    $path = Join-Path $folder "$name.nfproj"
    [IO.File]::WriteAllText($path, $project)
    & $MSBuildPath $path /t:Rebuild /p:Configuration=Release /warnaserror /v:quiet /nologo
    if ($LASTEXITCODE -ne 0) { throw "MCU build failed: $count" }
    $built = Join-Path $folder 'bin\Release'
    if (@(Get-ChildItem -LiteralPath $built | Where-Object { $_.Name -eq 'VagabondK.nF.DNP3.Mapping.dll' -or $_.Name -like '*Outstation*' -or $_.Name -like '*Generator*' -or $_.Name -like '*CodeAnalysis*' }).Count) { throw 'Unexpected MCU dependency.' }
    $sizes += [pscustomobject]@{
        Points = $count; AppPEBytes = (Get-Item -LiteralPath (Join-Path $built "$name.pe")).Length
        CorePEBytes = (Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.DNP3.pe')).Length
        MasterPEBytes = (Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.DNP3.Master.pe')).Length
        DeploymentBytes = (Get-Item -LiteralPath (Join-Path $built "$name.bin")).Length
        ImageSHA256 = (Get-FileHash -LiteralPath (Join-Path $built "$name.bin")).Hash
    }
}
$sizes | Export-Csv -LiteralPath (Join-Path $output 'sizes.csv') -NoTypeInformation
function Deploy([string]$image, [string]$label) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        & (Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe') --nanodevice --deploy --serialport COM3 --image $image --suppressnanoffversioncheck *>&1 |
            Tee-Object -FilePath (Join-Path $output "$label-deploy-$attempt.log")
        if ($LASTEXITCODE -eq 0) { return }
        if ($attempt -eq 3) { throw "Deployment failed: $label" }
        Write-Warning "Deploy failed: $label; retrying after 10 seconds."
        Start-Sleep -Seconds 10
    }
}
function Wait-MasterMetrics([int]$count) {
    $deadline = [Environment]::TickCount64 + 240000
    $request = [byte[]]@(0x44,0x4d,0x4d,0x31)
    $lastError = ''
    while ([Environment]::TickCount64 -lt $deadline) {
        $client = [Net.Sockets.UdpClient]::new()
        try {
            $client.Client.ReceiveTimeout = 1000
            $client.Connect('192.168.0.187',20002)
            $null = $client.Send($request,$request.Length)
            $remote = [Net.IPEndPoint]::new([Net.IPAddress]::Any,0)
            $reply = $client.Receive([ref]$remote)
            if ($reply.Length -ge 4 -and ($reply[0..3] -join ',') -eq '68,77,77,69') {
                $failure = [Text.Encoding]::UTF8.GetString($reply,4,$reply.Length-4)
                $failure | Set-Content -LiteralPath (Join-Path $output "$count\mcu-error.log")
                throw "MCU reported failure: $failure"
            }
            if ($reply.Length -ne 68 -or ($reply[0..3] -join ',') -ne ($request -join ',')) { throw 'Invalid diagnostic packet.' }
            $values = [uint32[]]::new(16)
            for ($i = 0; $i -lt 16; $i++) {
                $p = 4 + $i * 4
                $values[$i] = ([uint32]$reply[$p] -shl 24) -bor ([uint32]$reply[$p+1] -shl 16) -bor ([uint32]$reply[$p+2] -shl 8) -bor [uint32]$reply[$p+3]
            }
            if ($values[0] -ne $count -or $values[14] -ne 0 -or $values[15] -ne 0x600D) { throw 'MCU identity/debugger/completion mismatch.' }
            return ,$values
        }
        catch [Net.Sockets.SocketException] { $lastError = $_.Exception.Message; Write-Verbose "Waiting for MCU completion: $lastError" }
        finally { $client.Dispose() }
        Start-Sleep -Seconds 1
    }
    throw "MCU completion timeout: $count; $lastError"
}
$changed = $false
try {
    foreach ($size in $sizes) {
        $count = [int]$size.Points
        $folder = Join-Path $output "$count"
        $peer = $null
        $tail = $null
        $errors = $null
        try {
            $args = if ($count -eq 7) { '20000 physical 192.168.0.210' } else { '20000 standard 192.168.0.210' }
            $start = [Diagnostics.ProcessStartInfo]::new($native,$args)
            $start.UseShellExecute = $false
            $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
            $peer = [Diagnostics.Process]::Start($start)
            $errors = $peer.StandardError.ReadToEndAsync()
            $prefix = [Collections.Generic.List[string]]::new()
            do {
                $line = $peer.StandardOutput.ReadLineAsync()
                if (-not $line.Wait(10000) -or $null -eq $line.Result) { throw 'Independent Outstation readiness failed.' }
                $prefix.Add($line.Result)
            } while ($line.Result -ne 'READY')
            if ($count -ne 7) {
                $peer.StandardInput.WriteLine('update 0'); $peer.StandardInput.Flush()
                do {
                    $line = $peer.StandardOutput.ReadLineAsync()
                    if (-not $line.Wait(10000) -or $null -eq $line.Result) { throw 'Outstation initialization failed.' }
                    $prefix.Add($line.Result)
                } while ($line.Result -ne 'UPDATED 0')
            }
            $tail = $peer.StandardOutput.ReadToEndAsync()
            $changed = $true
            Deploy (Join-Path $folder "bin\Release\Dnp3MasterMapping$count.bin") "$count"
            $values = Wait-MasterMetrics $count
            if ($count -eq 7) {
                if ($values[4] -lt 6000 -or $values[5] -ne 1344 -or $values[6] -ne 448) { throw 'Sample validation totals mismatch.' }
                $result = [pscustomobject]@{ Points=7; Checks=$values[4]; Events=$values[5]; Unsolicited=$values[6]; Measurements=$values[7] }
            } else {
                if ($values[7] -ne 20 -or $values[8] -ne 9800 -or $values[9] -lt 2 -or $values[6] -lt $values[4] -or $values[10] -lt $values[4]) { throw 'Scaling replay/heap regression.' }
                $result = [pscustomobject]@{
                    Points=$count; ModelBytes=[long]$values[1]-$values[2]; BindingAndLockBytes=[long]$values[2]-$values[3]
                    CaptureStorageBytes=[long]$values[3]-$values[11]
                    ReplayIterations=$values[7]; MeasurementsPerIteration=490; Fragments=$values[9]; ReplayMilliseconds=$values[5]
                    HeapBefore=$values[4]; HeapAfter=$values[6]; HeapFinal=$values[10]; RetainedGrowthBytes=[math]::Max(0,[long]$values[4]-[long]$values[6])
                }
            }
            $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'measurements.json')
            $result | ConvertTo-Json -Compress | Write-Output
            $peer.StandardInput.WriteLine('quit'); $peer.StandardInput.Flush()
            if (-not $peer.WaitForExit(5000) -or $peer.ExitCode -ne 0) { throw 'Independent Outstation shutdown failed.' }
        }
        finally {
            if ($peer) {
                if (-not $peer.HasExited) { Stop-Process -Id $peer.Id; $peer.WaitForExit() }
                if ($tail) { ($prefix -join "`n") + "`n" + $tail.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $folder 'peer.log') }
                if ($errors) { $errors.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $folder 'peer-error.log') }
                $peer.Dispose()
            }
        }
        Start-Sleep -Seconds 5
    }
}
finally {
    if ($changed) {
        Start-Sleep -Seconds 5
        try { & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $output 'restored-modbus.log') }
        catch {
            Write-Warning "Restore failed; retrying once: $_"
            Start-Sleep -Seconds 5
            & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $output 'restored-modbus-retry.log')
        }
    }
}
Write-Output "DNP3 MASTER MAPPING HARDWARE PASS: original Modbus restored; $output"
