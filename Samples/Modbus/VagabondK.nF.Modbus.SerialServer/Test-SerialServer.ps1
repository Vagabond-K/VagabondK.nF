param(
    [string]$PortName = 'COM6',
    [ValidateSet('Rtu', 'Ascii')]
    [string]$Protocol = 'Rtu',
    [bool]$RtsEnable = $true,
    [bool]$DtrEnable = $true,
    [switch]$Extended,
    [switch]$Restore,
    [switch]$Endurance,
    [ValidateRange(20, 604800)]
    [int]$EnduranceSeconds = 7200,
    [string]$LogPath = (Join-Path $PSScriptRoot ("bin\endurance-{0}.csv" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [string]$BackupPath = (Join-Path $PSScriptRoot 'bin\serial-backup.clixml'),
    [ValidateRange(1, 10000)]
    [int]$Iterations = 100
)

$ErrorActionPreference = 'Stop'
if (-not ('System.IO.Ports.SerialPort' -as [type])) {
    throw 'System.IO.Ports is required. Run in Windows PowerShell 5.1 or load System.IO.Ports.dll first.'
}

function Get-Crc([byte[]]$Bytes, [int]$Count) {
    [int]$crc = 0xFFFF
    for ($i = 0; $i -lt $Count; $i++) {
        $crc = $crc -bxor [int]$Bytes[$i]
        for ($bit = 0; $bit -lt 8; $bit++) {
            if (($crc -band 1) -ne 0) { $crc = ($crc -shr 1) -bxor 0xA001 }
            else { $crc = $crc -shr 1 }
        }
    }
    return $crc
}

function Read-Exact([int]$Count, [DateTime]$Deadline) {
    $bytes = [byte[]]::new($Count)
    $offset = 0
    while ($offset -lt $Count) {
        $remaining = [int]($Deadline - [DateTime]::UtcNow).TotalMilliseconds
        if ($remaining -le 0) { throw "RTU timeout: received $offset of $Count bytes." }
        $port.ReadTimeout = $remaining
        try { $read = $port.Read($bytes, $offset, $Count - $offset) }
        catch {
            if ($_.Exception.GetBaseException() -is [TimeoutException]) {
                throw "RTU timeout: received $offset of $Count bytes in this response segment."
            }
            throw
        }
        if ($read -eq 0) { throw 'Serial read returned no bytes.' }
        $offset += $read
    }
    return ,$bytes
}

function Send-Request([byte[]]$Pdu, [byte]$Unit = 1, [switch]$Corrupt) {
    Start-Sleep -Milliseconds 100
    if ($port.BytesToRead -ne 0) { throw 'Unexpected pending serial bytes; refusing another request.' }
    $frame = New-RequestFrame $Pdu $Unit -Corrupt:$Corrupt
    $port.Write($frame, 0, $frame.Length)
}

function New-RequestFrame([byte[]]$Pdu, [byte]$Unit = 1, [switch]$Corrupt) {
    if ($Protocol -eq 'Ascii') {
        $sum = [int]$Unit
        $text = ':' + $Unit.ToString('X2')
        foreach ($value in $Pdu) { $sum += $value; $text += $value.ToString('X2') }
        $lrc = (-$sum) -band 255
        if ($Corrupt) { $lrc = $lrc -bxor 1 }
        return ,([Text.Encoding]::ASCII.GetBytes($text + $lrc.ToString('X2') + "`r`n"))
    }
    $frame = [byte[]]::new($Pdu.Length + 3)
    $frame[0] = $Unit
    [Array]::Copy($Pdu, 0, $frame, 1, $Pdu.Length)
    $crc = Get-Crc $frame ($frame.Length - 2)
    $frame[$frame.Length - 2] = $crc -band 255
    $frame[$frame.Length - 1] = $crc -shr 8
    if ($Corrupt) { $frame[$frame.Length - 1] = $frame[$frame.Length - 1] -bxor 1 }
    return ,$frame
}

function Read-AsciiReply([DateTime]$Deadline) {
    $bytes = [Collections.Generic.List[byte]]::new()
    while ($true) {
        $value = (Read-Exact 1 $Deadline)[0]
        $bytes.Add($value)
        if ($bytes.Count -gt 513) { throw 'ASCII response exceeds maximum length.' }
        if ($value -eq 10) { break }
    }
    $text = [Text.Encoding]::ASCII.GetString($bytes.ToArray())
    if ($text -cnotmatch '^:([0-9A-F]{2}){3,255}\r\n$') { throw 'Invalid ASCII response framing/hex.' }
    $decoded = [byte[]]::new(($text.Length - 3) / 2)
    $sum = 0
    for ($i = 0; $i -lt $decoded.Length; $i++) {
        $decoded[$i] = [Convert]::ToByte($text.Substring(1 + $i * 2, 2), 16)
        $sum += $decoded[$i]
    }
    if (($sum -band 255) -ne 0) { throw 'Response LRC mismatch.' }
    if ($decoded[0] -ne 1) { throw 'Response unit mismatch.' }
    return ,([byte[]]$decoded[1..($decoded.Length - 2)])
}

function Assert-Silent {
    Start-Sleep -Milliseconds 500
    if ($port.BytesToRead -ne 0) { throw 'Unexpected response to broadcast or corrupt CRC.' }
}

function Exchange([byte[]]$Pdu, [int]$ExceptionCode = 0) {
    Send-Request $Pdu
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    if ($Protocol -eq 'Ascii') {
        $reply = Read-AsciiReply $deadline
        if ($ExceptionCode -ne 0) { Check-Equal ([byte[]]@(($Pdu[0] -bor 128), $ExceptionCode)) $reply }
        elseif ($reply[0] -ne $Pdu[0]) { throw 'Response function mismatch.' }
        return ,$reply
    }
    $head = Read-Exact 3 $deadline
    if ($head[0] -ne 1) { throw 'Response unit mismatch.' }
    if ($ExceptionCode -ne 0) {
        if ($head[1] -ne ($Pdu[0] -bor 128) -or $head[2] -ne $ExceptionCode) {
            throw 'Unexpected Modbus exception response.'
        }
        $remaining = 2
    }
    else {
        if ($head[1] -ne $Pdu[0]) { throw "Response function mismatch: $($head[1])." }
        $remaining = if ($Pdu[0] -le 4) { [int]$head[2] + 2 } else { 5 }
    }
    $tail = Read-Exact $remaining $deadline
    $frame = [byte[]]($head + $tail)
    $crc = Get-Crc $frame ($frame.Length - 2)
    if ($frame[$frame.Length - 2] -ne ($crc -band 255) -or
        $frame[$frame.Length - 1] -ne ($crc -shr 8)) { throw 'Response CRC mismatch.' }
    return ,([byte[]]$frame[1..($frame.Length - 3)])
}

function Check-Equal([byte[]]$Expected, [byte[]]$Actual) {
    if ($Actual.Length -ne $Expected.Length) { throw 'Response length mismatch.' }
    for ($i = 0; $i -lt $Expected.Length; $i++) {
        if ($Actual[$i] -ne $Expected[$i]) { throw "Response mismatch at PDU byte $i." }
    }
}

function Read-Area([byte]$Function, [int]$Count) {
    $reply = Exchange ([byte[]]@($Function, 0, 0, ($Count -shr 8), ($Count -band 255)))
    $byteCount = if ($Function -le 2) { [int][Math]::Ceiling($Count / 8.0) } else { $Count * 2 }
    if ($reply.Length -ne $byteCount + 2 -or $reply[1] -ne $byteCount) {
        throw 'Read response byte count mismatch.'
    }
    return ,([byte[]]$reply[2..($reply.Length - 1)])
}

function Write-Multiple([byte]$Function, [int]$Count, [byte[]]$Data, [byte]$Unit = 1) {
    $pdu = [byte[]](@($Function, 0, 0, ($Count -shr 8), ($Count -band 255), $Data.Length) + $Data)
    if ($Unit -eq 0) { Send-Request $pdu 0; Assert-Silent }
    else {
        $reply = Exchange $pdu
        Check-Equal ([byte[]]@($Function, 0, 0, ($Count -shr 8), ($Count -band 255))) $reply
    }
}

function Test-Extended {
    if (Test-Path -LiteralPath $BackupPath) { throw "Existing recovery file: $BackupPath. Run with -Restore first." }
    Write-Output 'Snapshot: holding registers 0..124.'
    $savedRegisters = Read-Area 3 125
    Write-Output 'Snapshot: coils 0..1999.'
    $savedCoils = Read-Area 1 2000
    $null = New-Item -ItemType Directory -Path (Split-Path $BackupPath) -Force
    [pscustomobject]@{
        Version = 1; PortName = $PortName; BaudRate = 9600; Unit = 1; Protocol = $Protocol
        Registers = $savedRegisters; Coils = $savedCoils
    } | Export-Clixml -LiteralPath $BackupPath -NoClobber
    $testError = $null
    try {
        Write-Output 'Testing all 8 functions and maximum quantities.'
        $discrete = Read-Area 2 2000
        if ($discrete[0] -ne 5) { throw 'Discrete inputs 0..3 mismatch.' }
        for ($i = 1; $i -lt $discrete.Length; $i++) {
            if ($discrete[$i] -ne 0) { throw 'Unexpected discrete input.' }
        }
        $inputs = Read-Area 4 125
        Check-Equal ([byte[]]@(0,100,0,200,1,44)) ([byte[]]$inputs[0..5])
        $singleCoil = [byte[]]@(5,0,0,255,0)
        Check-Equal $singleCoil (Exchange $singleCoil)
        if ((Read-Area 1 1)[0] -ne 1) { throw 'Single coil write mismatch.' }
        $singleRegister = [byte[]]@(6,0,0,0xAB,0xCD)
        Check-Equal $singleRegister (Exchange $singleRegister)
        Check-Equal ([byte[]]@(0xAB,0xCD)) (Read-Area 3 1)
        $coilPattern = [byte[]]::new(246)
        for ($i = 0; $i -lt $coilPattern.Length; $i++) { $coilPattern[$i] = 0x55 }
        foreach ($bytes in @(110,111,112,230,231,232)) {
            $aduLength = if ($Protocol -eq 'Ascii') { 2 * $bytes + 19 } else { $bytes + 9 }
            Write-Output "Multiple-coil request boundary: $aduLength bytes."
            $pattern = [byte[]]$coilPattern[0..($bytes - 1)]
            Write-Multiple 15 ($bytes * 8) $pattern
            Check-Equal $pattern (Read-Area 1 ($bytes * 8))
        }
        Write-Output 'Maximum multiple-coil write: 1968 coils.'
        Write-Multiple 15 1968 $coilPattern
        Check-Equal $coilPattern (Read-Area 1 1968)
        $registerPattern = [byte[]]::new(246)
        for ($i = 0; $i -lt 123; $i++) {
            $value = 65535 - $i
            $registerPattern[$i * 2] = $value -shr 8
            $registerPattern[$i * 2 + 1] = $value -band 255
        }
        Write-Output 'Maximum multiple-register write: 123 registers.'
        Write-Multiple 16 123 $registerPattern
        Check-Equal $registerPattern (Read-Area 3 123)
        $maximum = Read-Area 3 125
        Write-Output 'Testing broadcast writes and exceptions.'
        Write-Output 'Broadcast single coil.'
        Send-Request ([byte[]]@(5,0,0,0,0)) 0
        Assert-Silent
        if ((Read-Area 1 1)[0] -ne 0) { throw 'Broadcast single coil mismatch.' }
        Write-Output 'Broadcast single register.'
        Send-Request ([byte[]]@(6,0,0,0x12,0x34)) 0
        Assert-Silent
        Check-Equal ([byte[]]@(0x12,0x34)) (Read-Area 3 1)
        Write-Output 'Broadcast multiple coils.'
        Write-Multiple 15 2 ([byte[]]@(2)) 0
        if ((Read-Area 1 2)[0] -ne 2) { throw 'Broadcast multiple coils mismatch.' }
        Write-Output 'Broadcast multiple registers.'
        Write-Multiple 16 2 ([byte[]]@(0,42,0,43)) 0
        Check-Equal ([byte[]]@(0,42,0,43)) (Read-Area 3 2)
        Write-Output 'Illegal address exception.'
        $null = Exchange ([byte[]]@(3,0,128,0,1)) 2
        Write-Output 'Illegal quantity exception.'
        $null = Exchange ([byte[]]@(3,0,0,0,126)) 3
        Write-Output 'Illegal function exception.'
        $null = Exchange ([byte[]]@(0x7F)) 1
        Write-Output 'Maximum ADU with invalid multiple-coil byte count (RTU 256 / ASCII 513).'
        $null = Exchange ([byte[]](@(15,0,0,7,176,247) + [byte[]]::new(247))) 3
        $maximum = Read-Area 3 125
        $watch = [Diagnostics.Stopwatch]::StartNew()
        for ($i = 1; $i -le $Iterations; $i++) {
            Check-Equal $maximum (Read-Area 3 125)
            if ($i % 25 -eq 0) { Write-Output "Maximum reads: $i / $Iterations" }
        }
        Write-Output "Maximum reads PASS: $Iterations in $([Math]::Round($watch.Elapsed.TotalSeconds,1))s."
        Write-Output 'Idle 12s without a request, then recover.'
        Start-Sleep -Seconds 12
        Check-Equal $maximum (Read-Area 3 125)
        Write-Output 'Partial request timeout and recovery.'
        if ($Protocol -eq 'Ascii') {
            $partial = [Text.Encoding]::ASCII.GetBytes(':0103')
            $port.Write($partial, 0, $partial.Length)
        }
        else { Send-Request ([byte[]]@(3)) }
        Start-Sleep -Milliseconds 3500
        Assert-Silent
        Check-Equal $maximum (Read-Area 3 125)
        $before = Exchange ([byte[]]@(4,0,120,0,6))
        for ($i = 0; $i -lt 10; $i++) {
            Send-Request ([byte[]]@(3,0,0,0,1)) -Corrupt
            Assert-Silent
            # 다음 요청 전에 샘플의 50ms 재동기화가 끝나도록 기다립니다.
            Start-Sleep -Milliseconds 200
            Check-Equal $maximum (Read-Area 3 125)
        }
        $after = Exchange ([byte[]]@(4,0,120,0,6))
        if ($before.Length -ne 14 -or $after.Length -ne 14 -or $after[1] -ne 12) {
            throw 'Unexpected sample diagnostics response.'
        }
        $crcBefore = ([int]$before[8] -shl 8) -bor [int]$before[9]
        $crcAfter = ([int]$after[8] -shl 8) -bor [int]$after[9]
        if ((($crcAfter - $crcBefore) -band 65535) -ne 10 -or
            (([int]$after[4] -shl 8) -bor [int]$after[5]) -eq 0 -or
            (([int]$after[6] -shl 8) -bor [int]$after[7]) -eq 0 -or
            (([int]$after[12] -shl 8) -bor [int]$after[13]) -ne $(if ($Protocol -eq 'Ascii') { 7 } else { 5 })) {
            throw 'Sample idle/partial-timeout/CRC diagnostics mismatch.'
        }
        if ($Protocol -eq 'Ascii') { Test-AsciiRecovery $maximum }
        Write-Output "Extended PASS ($Protocol): 8 functions, frame boundaries, maximum quantities, 4 broadcast writes, exceptions, idle/partial recovery, 10 checksum rejections/recoveries and diagnostics."
    }
    catch {
        $testError = $_
        throw
    }
    finally {
        try { Restore-Snapshot $savedRegisters $savedCoils }
        catch {
            if ($null -ne $testError) {
                throw "Test failed: $($testError.Exception.Message) Restoration also failed: $($_.Exception.Message) Recovery file retained: $BackupPath"
            }
            throw
        }
    }
}

function Test-AsciiRecovery([byte[]]$Maximum) {
    $valid = New-RequestFrame ([byte[]]@(3,0,0,0,125))
    foreach ($kind in @('Hex', 'CRLF', 'Oversized', 'InterCharacter')) {
        $before = Exchange ([byte[]]@(4,0,120,0,6))
        switch ($kind) {
            'Hex' { $bad = [Text.Encoding]::ASCII.GetBytes(":01G300000001FB`r`n"); $status = 8 }
            'CRLF' { $bad = [Text.Encoding]::ASCII.GetBytes(":010300000001FB0`n"); $status = 3 }
            'Oversized' { $bad = [Text.Encoding]::ASCII.GetBytes(':' + ('0' * 513)); $status = 4 }
            'InterCharacter' { $bad = [Text.Encoding]::ASCII.GetBytes(':0103'); $status = 2 }
        }
        $port.Write($bad, 0, $bad.Length)
        Start-Sleep -Milliseconds 1600
        Assert-Silent
        Check-Equal $Maximum (Read-Area 3 125)
        $after = Exchange ([byte[]]@(4,0,120,0,6))
        $index = if ($status -eq 2) { 6 } else { 10 }
        $delta = ((([int]$after[$index] -shl 8) -bor [int]$after[$index+1]) -
            (([int]$before[$index] -shl 8) -bor [int]$before[$index+1])) -band 65535
        if ($delta -ne 1 -or (([int]$after[12] -shl 8) -bor [int]$after[13]) -ne $status) {
            throw "ASCII $kind diagnostics mismatch."
        }
        Write-Output "ASCII $kind rejection/recovery PASS."
    }
    # 새 콜론은 미완성 프레임을 대체하며 1초 미만의 간격은 허용합니다.
    $prefix = [Text.Encoding]::ASCII.GetBytes(':0103')
    $port.Write($prefix, 0, $prefix.Length)
    Start-Sleep -Milliseconds 300
    $port.Write($valid, 0, 7)
    Start-Sleep -Milliseconds 300
    $port.Write($valid, 7, $valid.Length - 7)
    $reply = Read-AsciiReply ([DateTime]::UtcNow.AddSeconds(4))
    Check-Equal ([byte[]](@(3,250) + $Maximum)) $reply
    Write-Output 'ASCII colon restart / 300ms character gap PASS.'
}

function Assert-Snapshot($Snapshot) {
    $savedProtocol = if ($null -eq $Snapshot.Protocol) { 'Rtu' } else { $Snapshot.Protocol }
    if ($Snapshot.Version -ne 1 -or $Snapshot.PortName -ne $PortName -or
        $Snapshot.BaudRate -ne 9600 -or $Snapshot.Unit -ne 1 -or $savedProtocol -ne $Protocol) {
        throw 'Recovery file version or target does not match this RTU sample.'
    }
    foreach ($name in @('Registers', 'Coils')) {
        $data = $Snapshot.$name
        if ($null -eq $data -or $data.Count -ne 250) { throw "Invalid recovery data: $name." }
        foreach ($value in $data) {
            if ($value -isnot [byte]) { throw "Invalid recovery byte in $name." }
        }
    }
}

function Test-Endurance {
    if (Test-Path -LiteralPath $LogPath) { throw "Existing endurance log: $LogPath" }
    $null = New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($LogPath))) -Force
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $reads = 0
    $cycles = 0
    $maxReadMs = 0.0
    function Write-EnduranceLog([string]$State, [string]$Detail) {
        [pscustomobject]@{
            Utc = [DateTime]::UtcNow.ToString('o')
            State = $State
            ElapsedSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 3)
            Reads = $reads
            FaultCycles = $cycles
            MaxReadMilliseconds = [Math]::Round($maxReadMs, 1)
            Detail = $Detail
        } | Export-Csv -LiteralPath $LogPath -Append -NoClobber -NoTypeInformation
    }
    function Get-DiagnosticValue([byte[]]$Reply, [int]$Index) {
        if ($Reply.Length -ne 14 -or $Reply[1] -ne 12) { throw 'Invalid diagnostics response.' }
        return (([int]$Reply[2 + $Index * 2] -shl 8) -bor [int]$Reply[3 + $Index * 2])
    }
    Write-EnduranceLog 'Started' "Port=$PortName; duration=${EnduranceSeconds}s; read-only; MCU heap telemetry unavailable."
    try {
        $registers = Read-Area 3 125
        $coils = Read-Area 1 2000
        $nextCycle = 0.0
        $nextProgress = 60.0
        while ($clock.Elapsed.TotalSeconds -lt $EnduranceSeconds) {
            $readClock = [Diagnostics.Stopwatch]::StartNew()
            Check-Equal $registers (Read-Area 3 125)
            $maxReadMs = [Math]::Max($maxReadMs, $readClock.Elapsed.TotalMilliseconds)
            $reads++
            if ($clock.Elapsed.TotalSeconds -ge $nextCycle) {
                $before = Exchange ([byte[]]@(4,0,120,0,6))
                Send-Request ([byte[]]@(3,0,0,0,1)) -Corrupt
                Assert-Silent
                Start-Sleep -Milliseconds 200
                $readClock.Restart()
                Check-Equal $registers (Read-Area 3 125)
                $crcRecoveryMs = [Math]::Round($readClock.Elapsed.TotalMilliseconds, 1)
                Send-Request ([byte[]]@(3))
                Start-Sleep -Milliseconds 3500
                Assert-Silent
                $readClock.Restart()
                Check-Equal $registers (Read-Area 3 125)
                $partialRecoveryMs = [Math]::Round($readClock.Elapsed.TotalMilliseconds, 1)
                Start-Sleep -Seconds 12
                $readClock.Restart()
                Check-Equal $registers (Read-Area 3 125)
                $idleRecoveryMs = [Math]::Round($readClock.Elapsed.TotalMilliseconds, 1)
                $after = Exchange ([byte[]]@(4,0,120,0,6))
                foreach ($index in @(2,3)) {
                    if ((((Get-DiagnosticValue $after $index) - (Get-DiagnosticValue $before $index)) -band 65535) -ne 1) {
                        throw "Unexpected diagnostic counter delta: $index."
                    }
                }
                if ((((Get-DiagnosticValue $after 1) - (Get-DiagnosticValue $before 1)) -band 65535) -eq 0 -or
                    (Get-DiagnosticValue $after 5) -ne 2 -or
                    (Get-DiagnosticValue $after 4) -ne (Get-DiagnosticValue $before 4)) {
                    throw 'Idle, last-error or unexpected-error diagnostics mismatch.'
                }
                Check-Equal $coils (Read-Area 1 2000)
                $cycles++
                Write-EnduranceLog 'FaultCyclePassed' "CRC recovery=${crcRecoveryMs}ms; partial recovery=${partialRecoveryMs}ms; idle recovery=${idleRecoveryMs}ms; diagnostics=$([BitConverter]::ToString($after))"
                $nextCycle = $clock.Elapsed.TotalSeconds + 60
            }
            if ($clock.Elapsed.TotalSeconds -ge $nextProgress) {
                Write-EnduranceLog 'Progress' 'Maximum register reads verified.'
                Write-Output "Endurance: $reads maximum reads, $cycles fault cycles, $([Math]::Round($clock.Elapsed.TotalSeconds))s."
                $nextProgress = $clock.Elapsed.TotalSeconds + 60
            }
        }
        Check-Equal $registers (Read-Area 3 125)
        Check-Equal $coils (Read-Area 1 2000)
        Write-EnduranceLog 'Passed' 'All responses and recovery counters verified; holding registers/coils unchanged.'
        Write-Output "Endurance PASS: $reads maximum reads, $cycles fault cycles. Log: $LogPath"
    }
    catch {
        Write-EnduranceLog 'Failed' $_.Exception.Message
        throw
    }
}

function Restore-Snapshot([byte[]]$Registers, [byte[]]$Coils) {
    if ($Registers.Length -ne 250 -or $Coils.Length -ne 250) { throw 'Invalid snapshot lengths; refusing restoration.' }
    # 작은 복원 프레임을 사용해 시험 대상인 최대 크기 프레임 처리 경로에 의존하지 않습니다.
    for ($index = 0; $index -lt 125; $index += 10) {
        $count = [Math]::Min(10, 125 - $index)
        $data = [byte[]]$Registers[($index*2)..(($index+$count)*2-1)]
        $pdu = [byte[]](@(16,0,$index,0,$count,$data.Length) + $data)
        Check-Equal ([byte[]]@(16,0,$index,0,$count)) (Exchange $pdu)
    }
    for ($index = 0; $index -lt 1968; $index += 160) {
        $count = [Math]::Min(160,1968-$index)
        $data = [byte[]]$Coils[($index/8)..(($index+$count)/8-1)]
        $pdu = [byte[]](@(15,($index -shr 8),($index -band 255),0,$count,$data.Length) + $data)
        Check-Equal ([byte[]]@(15,($index -shr 8),($index -band 255),0,$count)) (Exchange $pdu)
    }
    Check-Equal $Registers (Read-Area 3 125)
    Check-Equal $Coils (Read-Area 1 2000)
    if (Test-Path -LiteralPath $BackupPath) { Remove-Item -LiteralPath $BackupPath }
    Write-Output 'Sample registers and coils restored and verified.'
}

if (([int]$Extended.IsPresent + [int]$Restore.IsPresent + [int]$Endurance.IsPresent) -gt 1) {
    throw 'Use only one of -Extended, -Restore or -Endurance.'
}
if ($Endurance -and $PortName -ne 'COM6') { throw 'Endurance mode is restricted to the verified COM6 RS-485 adapter.' }
if ($Endurance -and $Protocol -ne 'Rtu') { throw 'Endurance mode currently supports RTU only.' }
if ($Restore) {
    $backup = Import-Clixml -LiteralPath $BackupPath
    Assert-Snapshot $backup
}
elseif ($Extended -and (Test-Path -LiteralPath $BackupPath)) {
    throw "Existing recovery file: $BackupPath. Run with -Restore first."
}

$port = [System.IO.Ports.SerialPort]::new($PortName, 9600,
    [System.IO.Ports.Parity]::Even, $(if ($Protocol -eq 'Ascii') { 7 } else { 8 }), [System.IO.Ports.StopBits]::One)
try {
    $port.Handshake = [System.IO.Ports.Handshake]::None
    $port.RtsEnable = $RtsEnable
    $port.DtrEnable = $DtrEnable
    $port.ParityReplace = 0
    $port.ReadTimeout = 4000
    $port.WriteTimeout = 3000
    $port.Open()
    Start-Sleep -Milliseconds 500
    $port.DiscardInBuffer()
    if ($Restore) {
        Restore-Snapshot ([byte[]]$backup.Registers) ([byte[]]$backup.Coils)
        return
    }
    $reply = Exchange ([byte[]]@(3,0,0,0,10))
    Write-Output ([BitConverter]::ToString($reply))
    if ($reply.Length -ne 22 -or $reply[0] -ne 3 -or $reply[1] -ne 20) {
        throw 'Unexpected response header.'
    }
    $values = @(for ($i = 2; $i -lt 22; $i += 2) {
        ([int]$reply[$i] -shl 8) -bor [int]$reply[$i + 1]
    })
    if ($values[0] -ne 123 -or $values[1] -ne 456 -or $values[2] -ne 65535) {
        throw 'Unexpected sample register values.'
    }
    Write-Output "PASS: $PortName $Protocol 9600 $($port.DataBits)E1; RTS=$RtsEnable DTR=$DtrEnable; holding 0..2=123,456,65535."
    if ($Extended) { Test-Extended }
    if ($Endurance) { Test-Endurance }
}
finally {
    $port.Dispose()
}
