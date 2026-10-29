param(
    [Parameter(Mandatory = $true)]
    [string]$Address,
    [ValidateRange(1, 65535)]
    [int]$Port = 502,
    [switch]$Extended,
    [ValidateRange(1, 1000000)]
    [int]$Iterations = 1000,
    [ValidateRange(1, 1000)]
    [int]$ReconnectCount = 10,
    [switch]$TimeoutRecovery
)

$ErrorActionPreference = 'Stop'
if ($TimeoutRecovery -and -not $Extended) {
    throw '-TimeoutRecovery requires -Extended.'
}
$client = [System.Net.Sockets.TcpClient]::new()
$script:transaction = 0
$script:checks = 0

function Read-Exactly([int]$Count) {
    $buffer = [byte[]]::new($Count)
    $offset = 0
    while ($offset -lt $Count) {
        $read = $script:stream.Read($buffer, $offset, $Count - $offset)
        if ($read -eq 0) { throw 'Peer closed the connection.' }
        $offset += $read
    }
    return ,$buffer
}

function New-Request([byte[]]$Pdu) {
    $script:transaction = ($script:transaction + 1) -band 65535
    $id = $script:transaction
    $length = $Pdu.Length + 1
    $request = [byte[]]::new(7 + $Pdu.Length)
    $request[0] = [byte]($id -shr 8)
    $request[1] = [byte]($id -band 255)
    $request[4] = [byte]($length -shr 8)
    $request[5] = [byte]($length -band 255)
    $request[6] = 1
    [Array]::Copy($Pdu, 0, $request, 7, $Pdu.Length)
    return ,$request
}

function Receive-Pdu([int]$Id) {
    $header = Read-Exactly 7
    $receivedId = ([int]$header[0] -shl 8) -bor [int]$header[1]
    $receivedLength = ([int]$header[4] -shl 8) -bor [int]$header[5]
    if ($receivedId -ne $Id -or $header[2] -ne 0 -or $header[3] -ne 0 -or
        $header[6] -ne 1 -or $receivedLength -lt 2 -or $receivedLength -gt 254) {
        throw 'Invalid MBAP response.'
    }
    return ,(Read-Exactly ($receivedLength - 1))
}

function Invoke-Pdu([byte[]]$Pdu, [switch]$Fragment) {
    $request = New-Request $Pdu
    $id = $script:transaction
    if ($Fragment) {
        $script:stream.Write($request, 0, 3)
        Start-Sleep -Milliseconds 20
        $script:stream.Write($request, 3, $request.Length - 3)
    }
    else {
        $script:stream.Write($request, 0, $request.Length)
    }
    return ,(Receive-Pdu $id)
}

function Assert-Bytes([byte[]]$Actual, [byte[]]$Expected) {
    if ([BitConverter]::ToString($Actual) -ne [BitConverter]::ToString($Expected)) {
        throw ('Unexpected response: ' + [BitConverter]::ToString($Actual) +
            '; expected: ' + [BitConverter]::ToString($Expected))
    }
    $script:checks++
}

function Assert-Pdu([byte[]]$Request, [byte[]]$Expected, [switch]$Fragment) {
    $actual = Invoke-Pdu $Request -Fragment:$Fragment
    Assert-Bytes $actual $Expected
}

function Connect-Server {
    $script:client = [System.Net.Sockets.TcpClient]::new()
    $connect = $script:client.ConnectAsync($Address, $Port)
    if (-not $connect.Wait(5000)) { throw 'TCP connection timed out.' }
    $null = $connect.GetAwaiter().GetResult()
    $script:stream = $script:client.GetStream()
    $script:stream.ReadTimeout = 5000
    $script:stream.WriteTimeout = 5000
}

function New-RegisterWrite([byte[]]$ReadResponse) {
    if ($ReadResponse.Length -ne 248 -or $ReadResponse[0] -ne 3 -or $ReadResponse[1] -ne 246) {
        throw 'Cannot preserve 123 holding registers.'
    }
    $request = [byte[]]::new(252)
    [byte[]]@(16, 0, 0, 0, 123, 246) | ForEach-Object -Begin { $i = 0 } -Process {
        $request[$i++] = $_
    }
    [Array]::Copy($ReadResponse, 2, $request, 6, 246)
    return ,$request
}

function Read-HeapDiagnostics {
    $data = Invoke-Pdu @(4, 0, 120, 0, 8)
    if ($data.Length -ne 18 -or $data[0] -ne 4 -or $data[1] -ne 16) {
        throw 'Invalid heap diagnostics response.'
    }
    $words = @(for ($j = 2; $j -lt 18; $j += 2) { ([int]$data[$j] -shl 8) -bor [int]$data[$j + 1] })
    if ($words[0] -ne 0x4E45) {
        throw 'Heap diagnostics unavailable. Deploy the updated TCP Server sample and send at least 1000 requests.'
    }
    $requests = ([long]$words[1] -shl 16) -bor $words[2]
    $before = ([long]$words[3] -shl 16) -bor $words[4]
    $after = ([long]$words[5] -shl 16) -bor $words[6]
    Write-Output "HEAP after forced GC: requests=$requests, baseline=$before, free=$after, delta=$($after - $before), samples=$($words[7])"
}

function Assert-Closed {
    $closed = $false
    try { $closed = $script:stream.ReadByte() -eq -1 }
    catch [System.IO.IOException] {
        if ($_.Exception.InnerException -is [System.Net.Sockets.SocketException] -and
            $_.Exception.InnerException.SocketErrorCode -eq [System.Net.Sockets.SocketError]::ConnectionReset) {
            $closed = $true
        }
        else { throw }
    }
    if (-not $closed) { throw 'Expected the server to close this connection.' }
    $script:checks++
}

try {
    $client.Dispose()
    Connect-Server

    Assert-Pdu @(3, 0, 0, 0, 3) @(3, 6, 0, 123, 1, 200, 255, 255)
    Assert-Pdu @(4, 0, 0, 0, 3) @(4, 6, 0, 100, 0, 200, 1, 44)
    Assert-Pdu @(2, 0, 0, 0, 4) @(2, 1, 5)
    Assert-Pdu @(3, 0, 128, 0, 1) @(131, 2)
    Assert-Pdu @(3, 0, 0, 0, 3) @(3, 6, 0, 123, 1, 200, 255, 255) -Fragment

    # 이후 검증이 실패하더라도 복원할 수 있도록 시험용 주소의 값을 보관합니다.
    $originalBits = Invoke-Pdu @(1, 0, 10, 0, 2)
    $originalRegisters = Invoke-Pdu @(3, 0, 10, 0, 2)
    if ($originalBits.Length -ne 3 -or $originalBits[0] -ne 1 -or $originalBits[1] -ne 1 -or
        $originalRegisters.Length -ne 6 -or $originalRegisters[0] -ne 3 -or $originalRegisters[1] -ne 4) {
        throw 'Cannot preserve scratch addresses.'
    }
    try {
        Assert-Pdu @(5, 0, 10, 255, 0) @(5, 0, 10, 255, 0)
        $bits = Invoke-Pdu @(1, 0, 10, 0, 2)
        if ($bits.Length -ne 3 -or $bits[0] -ne 1 -or $bits[1] -ne 1 -or
            ($bits[2] -band 1) -ne 1) { throw 'Single coil write was not applied.' }
        $script:checks++
        Assert-Pdu @(15, 0, 10, 0, 2, 1, 2) @(15, 0, 10, 0, 2)
        Assert-Pdu @(1, 0, 10, 0, 2) @(1, 1, 2)
        Assert-Pdu @(6, 0, 10, 18, 52) @(6, 0, 10, 18, 52)
        Assert-Pdu @(3, 0, 10, 0, 1) @(3, 2, 18, 52)
        Assert-Pdu @(16, 0, 10, 0, 2, 4, 171, 205, 254, 220) @(16, 0, 10, 0, 2)
        Assert-Pdu @(3, 0, 10, 0, 2) @(3, 4, 171, 205, 254, 220)
        for ($i = 0; $i -lt 100; $i++) {
            Assert-Pdu @(3, 0, 0, 0, 3) @(3, 6, 0, 123, 1, 200, 255, 255)
        }
    }
    finally {
        Assert-Pdu @(15, 0, 10, 0, 2, 1, ($originalBits[2] -band 3)) @(15, 0, 10, 0, 2)
        Assert-Pdu @(16, 0, 10, 0, 2, 4, $originalRegisters[2], $originalRegisters[3],
            $originalRegisters[4], $originalRegisters[5]) @(16, 0, 10, 0, 2)
        Assert-Pdu @(1, 0, 10, 0, 2) $originalBits
        Assert-Pdu @(3, 0, 10, 0, 2) $originalRegisters
    }
    if ($Extended) {
        $baseline = Invoke-Pdu @(3, 0, 0, 0, 125)
        if ($baseline.Length -ne 252 -or $baseline[0] -ne 3 -or $baseline[1] -ne 250) {
            throw 'Invalid maximum register read.'
        }
        $script:checks++
        $saved = Invoke-Pdu @(3, 0, 0, 0, 123)
        $restore = New-RegisterWrite $saved
        $pattern = [byte[]]::new(252)
        [Array]::Copy($restore, $pattern, 252)
        for ($i = 0; $i -lt 246; $i++) { $pattern[6 + $i] = [byte](($i * 37) -band 255) }
        try {
            Assert-Pdu $pattern @(16, 0, 0, 0, 123)
            $expected = [byte[]]::new(248)
            $expected[0] = 3
            $expected[1] = 246
            [Array]::Copy($pattern, 6, $expected, 2, 246)
            Assert-Pdu @(3, 0, 0, 0, 123) $expected
            Assert-Pdu @(3, 0, 0, 0, 0) @(131, 3)
            Assert-Pdu @(3, 0, 0, 0, 126) @(131, 3)
            Assert-Pdu @(3, 255, 255, 0, 2) @(131, 2)
            Assert-Pdu @(5, 0, 0, 0, 1) @(133, 3)
            Assert-Pdu @(16, 0, 0, 0, 2, 2, 0, 1) @(144, 3)
            Assert-Pdu @(99) @(227, 1)
            Assert-Pdu @(3) @(131, 3)
            $before = Invoke-Pdu @(3, 0, 127, 0, 1)
            Assert-Pdu @(16, 0, 127, 0, 2, 4, 0, 1, 0, 2) @(144, 2)
            Assert-Pdu @(3, 0, 127, 0, 1) $before
        }
        finally {
            Assert-Pdu $restore @(16, 0, 0, 0, 123)
            Assert-Pdu @(3, 0, 0, 0, 125) $baseline
        }

        # 완전한 ADU 두 개를 한 번에 쓰고 두 응답을 모두 읽어 검증합니다.
        $first = New-Request @(3, 0, 0, 0, 125)
        $firstId = $script:transaction
        $second = New-Request @(3, 0, 0, 0, 125)
        $secondId = $script:transaction
        $combined = [byte[]]($first + $second)
        $script:stream.Write($combined, 0, $combined.Length)
        Assert-Bytes (Receive-Pdu $firstId) $baseline
        Assert-Bytes (Receive-Pdu $secondId) $baseline

        $clock = [Diagnostics.Stopwatch]::StartNew()
        for ($i = 0; $i -lt $Iterations; $i++) {
            Assert-Pdu @(3, 0, 0, 0, 125) $baseline
            if (($i + 1) % 2000 -eq 0) { Read-HeapDiagnostics }
        }
        $clock.Stop()
        Write-Output ("Repeated maximum reads: {0}; elapsed: {1:N1}s" -f $Iterations, $clock.Elapsed.TotalSeconds)

        for ($i = 0; $i -lt $ReconnectCount; $i++) {
            $client.Dispose()
            Connect-Server
            Assert-Pdu @(3, 0, 0, 0, 125) $baseline
        }
        # 잘못된 MBAP 헤더를 받으면 이 연결만 닫아야 합니다.
        $bad = [byte[]]@(0, 1, 0, 1, 0, 2, 1, 3)
        $script:stream.Write($bad, 0, $bad.Length)
        Assert-Closed
        $client.Dispose()
        Connect-Server
        Assert-Pdu @(3, 0, 0, 0, 125) $baseline
        Write-Output "Extended checks passed: boundaries, maximum writes, coalesced ADUs, $ReconnectCount reconnects, invalid MBAP recovery."
        if ($TimeoutRecovery) {
            $script:stream.ReadTimeout = 40000
            Assert-Closed
            $client.Dispose()
            Connect-Server
            Assert-Pdu @(3, 0, 0, 0, 125) $baseline
            $partial = [byte[]]@(0, 42, 0)
            $script:stream.Write($partial, 0, $partial.Length)
            $script:stream.ReadTimeout = 40000
            Assert-Closed
            $client.Dispose()
            Connect-Server
            Assert-Pdu @(3, 0, 0, 0, 125) $baseline
            Write-Output 'Timeout recovery passed: idle and partial MBAP, then new connection.'
        }
    }
    Write-Output "PASS: $script:checks checks; all 8 functions, fragmented request, exception, repeated reads; scratch values restored."
}
finally {
    $client.Dispose()
}
