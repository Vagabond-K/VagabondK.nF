param(
    [Parameter(Mandatory = $true)]
    [string]$Address
)

$ErrorActionPreference = 'Stop'
$client = [System.Net.Sockets.TcpClient]::new()
try {
    $task = $client.ConnectAsync($Address, 502)
    if (-not $task.Wait(5000)) { throw 'Self-test report server is not ready.' }
    $null = $task.GetAwaiter().GetResult()
    $stream = $client.GetStream()
    $stream.ReadTimeout = 5000
    $stream.WriteTimeout = 5000
    $request = [byte[]]@(0, 1, 0, 0, 0, 6, 1, 4, 0, 0, 0, 12)
    $stream.Write($request, 0, $request.Length)
    $reply = [byte[]]::new(33)
    $offset = 0
    while ($offset -lt $reply.Length) {
        $read = $stream.Read($reply, $offset, $reply.Length - $offset)
        if ($read -eq 0) { throw 'Incomplete self-test report.' }
        $offset += $read
    }
    $header = [byte[]]@(0, 1, 0, 0, 0, 27, 1, 4, 24)
    for ($i = 0; $i -lt $header.Length; $i++) {
        if ($reply[$i] -ne $header[$i]) { throw 'Unexpected self-test report header.' }
    }
    $words = @(for ($i = 9; $i -lt 33; $i += 2) { ([int]$reply[$i] -shl 8) -bor [int]$reply[$i + 1] })
    if ($words[0] -ne 0x600D -or $words[11] -ne 1000) { throw 'Self-test did not pass.' }
    $checks = ([long]$words[1] -shl 16) -bor $words[2]
    $rtuBefore = ([long]$words[3] -shl 16) -bor $words[4]
    $rtuAfter = ([long]$words[5] -shl 16) -bor $words[6]
    $asciiBefore = ([long]$words[7] -shl 16) -bor $words[8]
    $asciiAfter = ([long]$words[9] -shl 16) -bor $words[10]
    Write-Output "PASS: $checks assertions on MCU; 1000 repeated reads per serial transport."
    Write-Output "RTU GC free bytes: $rtuBefore -> $rtuAfter; delta=$($rtuAfter - $rtuBefore)"
    Write-Output "ASCII GC free bytes: $asciiBefore -> $asciiAfter; delta=$($asciiAfter - $asciiBefore)"
    Write-Output 'In-memory channels only; UART/RS485 and wire timing remain untested.'
}
finally {
    $client.Dispose()
}
