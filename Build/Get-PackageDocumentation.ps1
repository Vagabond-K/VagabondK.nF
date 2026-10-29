param([Parameter(Mandatory = $true)][string]$PackageId)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$group = $PackageId.Split('.')[2]
$projectDoc = Join-Path $root "Source\$group\$PackageId\README.md"
$source = if (Test-Path -LiteralPath $projectDoc) { $projectDoc } else { Join-Path $root "Source\$group\README.md" }
[xml]$metadata = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PackageMetadata.props')
$repository = [string]$metadata.Project.PropertyGroup.RepositoryUrl
$omit = '샘플과 검증|성능|실물|실측|시험|빌드.*(회귀|시험|패키지)|설치 및 검증|MCU 비용|코어.*분리 결과|참조와 검증|PC 도구 로컬 패키지|이전.*비교|탐색.*검증|복합.*검증'
$lines = [Collections.Generic.List[string]]::new()
$skipping = 0
$fence = ''
foreach ($line in Get-Content -LiteralPath $source -Encoding UTF8) {
    if ($line -match '^\s*(`{3,}|~{3,})') {
        $marker = $Matches[1].Substring(0, 1)
        if (-not $fence) { $fence = $marker } elseif ($fence -eq $marker) { $fence = '' }
        if (-not $skipping) { $lines.Add($line) }
        continue
    }
    if ($fence) {
        if (-not $skipping) { $lines.Add($line) }
        continue
    }
    if ($line -match '^(#{1,6})\s+(.+)$') {
        $level = $Matches[1].Length
        $heading = $Matches[2]
        if ($skipping -and $level -le $skipping) { $skipping = 0 }
        if ($skipping) { continue }
        if ($level -eq 1) { continue }
        $mappingSection = $source -ne $projectDoc -and $heading -match '^(빌드 시점|정적) 객체 매핑'
        if ($heading -match $omit -or ($mappingSection -and -not $PackageId.EndsWith('.Mapping'))) {
            $skipping = $level
            continue
        }
        if ($PackageId -eq 'VagabondK.nF.Modbus.Mapping' -and $level -eq 2 -and $heading -notmatch '빌드 시점 객체 매핑') {
            $skipping = $level
            continue
        }
        $line = '#' + $line
    }
    if ($skipping -or $line -match '^\[전체 프로젝트\]|^이 문서의 명령과 코드 경로는') { continue }
    $converted = [regex]::Replace($line, '(!?\[[^\]]*\])\(([^)\s]+)([^)]*)\)', {
        param($match)
        $target = $match.Groups[2].Value
        if ($target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:|^//') { return $match.Value }
        $parts = $target.Split('#', 2)
        $path = if ($parts[0]) { [IO.Path]::GetFullPath((Join-Path (Split-Path $source) $parts[0])) } else { $source }
        $relative = [IO.Path]::GetRelativePath($root, $path).Replace('\', '/')
        if ($relative.StartsWith('../')) { throw "Documentation link escapes repository: $target" }
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing documentation link target: $path" }
        $fragment = if ($parts.Count -eq 2) { '#' + $parts[1] } else { '' }
        return $match.Groups[1].Value + '(' + $repository + '/blob/HEAD/' + $relative + $fragment + $match.Groups[3].Value + ')'
    })
    $lines.Add($converted)
}
$documentation = ($lines -join "`n").Trim()
$documentation = [regex]::Replace($documentation, '(?ms)^```powershell\r?\n(?:(?!^```).)*?(?:Test-|Pack-|\\Tests\\)(?:(?!^```).)*?^```\s*', '')
$example = switch -Regex ($PackageId) {
    '^VagabondK\.nF\.IEC61850\.SCL$' {
@'
### 설치한 도구 실행하기

```powershell
.\tools\vk-nf-iec61850-scl input.scd GeneratedIec61850Model.cs
```

`input.scd`는 변환할 SCL 파일이고 `GeneratedIec61850Model.cs`는 생성할 C# 파일입니다.
지원하지 않는 요소·속성, 잘못된 참조와 값은 행 번호를 포함한 오류로 거부합니다.
입력 오류가 있으면 일부 모델만 출력하지 않으며 기존 출력 파일도 덮어쓰지 않습니다.
성공한 출력 파일을 MCU 프로젝트에 포함한 뒤 앱의 값 갱신·보고 처리와 제어 정책을 연결해야 합니다.
'@
    }
    '^VagabondK\.nF\.DNP3(?!\.Mapping$)' {
@'
### TCP Outstation을 구성하는 흐름

다음은 이미 연결된 `socket`, 앱에서 만든 `database`, `IDnp3ControlHandler` 구현인 `controls`가
있다고 가정한 연결 코드입니다. `database`에는 노출할 포인트 정의와 초기값을 미리 구성해야 합니다.
`DNP3.Outstation`과 `DNP3.TCP`를 함께 설치하면 실행할 수 있으며, 코어만으로 소켓 통신이 시작되지는 않습니다.

```csharp
using VagabondK.nF.DNP3;
using VagabondK.nF.DNP3.TCP;

var station = new Dnp3Station(database, 1, 2, controls);
var session = new Dnp3TcpSession(socket, station, 3000, 3000);
while (true)
    session.RunOnce();
```

### Master와 시리얼 전송

Master에서는 `IDnp3MasterHandler`를 구현해 값과 응답 정보를 받습니다.
다음의 `handler`는 앱에서 준비한 수신 처리기이고, `socket`은 연결을 마친 소켓입니다.

```csharp
var master = new Dnp3Master(2, 1, handler,
    responseTimeout: 30000, transportTimeout: 30000);
var session = new Dnp3TcpSession(socket, master, 3000, 3000);
session.Send(master.ReadClasses(15, System.Environment.TickCount64));
while (master.Busy)
    session.RunOnce(10);
```

`ReadClasses(15, ...)`는 Class 0~3 읽기 요청을 만들고 `Send`가 이를 전송합니다.
`RunOnce`는 요청을 새로 만드는 호출이 아니라 응답·확인·시간 제한 처리를 진행하는 호출입니다.
비요청 응답도 받으려면 요청이 끝난 뒤에도 수신 루프를 계속 실행하십시오.
시리얼 전송을 선택했다면 `DNP3.TCP` 대신 `DNP3.Serial`을 설치하고, 연 UART에 다음 세션을 연결합니다.

```csharp
using VagabondK.nF.DNP3.Serial;

var channel = new SerialPortChannel(port);
var session = new Dnp3SerialSession(channel, master,
    ackTimeoutMilliseconds: 3000, frameTimeoutMilliseconds: 3000);
session.RunOnce(10);
```

위 코드의 `port`는 앱이 설정하고 연 포트입니다. UART 핀 배치·속도·패리티와
RS-485 방향 제어 방식은 실제 보드와 어댑터에 맞게 지정해야 합니다.
TCP 연결이 끊어지면 기존 소켓을 해제하고 연결 상태를 초기화한 뒤 새 세션을 만드십시오.
Outstation의 값·이벤트 DB 수명과 연결 수명은 구분해서 관리해야 합니다.
'@
    }
    '^VagabondK\.nF\.BACnet(\.IP)?$' {
@'
### 값을 제공하는 저장소와 UDP 요청 처리

앱은 `IBacnetValueStore`로 객체 목록과 현재값, 쓰기 처리를 제공합니다.
아래는 읽기 전용 AnalogValue 0 하나를 제공하는 저장소입니다. 값은 float32의 원시 비트로 전달하며,
`0x437a0000`은 `250.0f`에 해당합니다.

```csharp
using System;
using VagabondK.nF.BACnet;

sealed class Values : IBacnetValueStore
{
    public int Count { get { return 1; } }
    public BacnetObject GetObject(int index)
    {
        return new BacnetObject(0, false);
    }
    public BacnetSample Read(int index)
    {
        return new BacnetSample(0x437a0000, true);
    }
    public void QueueWrite(int index, uint bits)
    {
        throw new InvalidOperationException("Read-only value.");
    }
}
```

네트워크 설정을 마친 뒤 `BACnet.IP` 패키지로 소켓과 Device를 연결합니다.
`12345`는 예제 Device 인스턴스이므로 같은 망의 다른 장치와 겹치지 않게 바꾸십시오.

```csharp
using System.Net;
using System.Net.Sockets;
using VagabondK.nF.BACnet;
using VagabondK.nF.BACnet.IP;

using (var socket = new Socket(AddressFamily.InterNetwork,
    SocketType.Dgram, ProtocolType.Udp))
{
    socket.Bind(new IPEndPoint(IPAddress.Any, 47808));
    var device = new BacnetDevice(new Values(), 12345, 0, "Evaluation device");
    var session = new BacnetIpSession(socket, device);
    session.ProcessNext(1000);
}
```

`ProcessNext`는 한 번만 호출한 예입니다. 계속 서비스하려면 앱의 루프에서 반복 호출하고,
잘못된 요청에서 발생하는 `BacnetProtocolException`을 기록한 뒤 다음 요청 처리 여부를 결정하십시오.
소켓 오류와 연결 종료까지 무시해서는 안 됩니다. 인증 기능이 없으므로 신뢰할 수 있는 망에서만 사용하십시오.
'@
    }
    '^VagabondK\.nF\.SNMP(\.UDP)?$' {
@'
### 스칼라 저장소와 UDP Agent

앱은 `ISnmpValueStore`로 OID와 값을 제공합니다. 아래는 Integer32 스칼라 하나를 읽기 전용으로 노출하는 예입니다.
`1.3.6.1.4.1.32473`은 문서 예제용 enterprise 번호이므로 실제 제품에는 적절한 OID를 사용하십시오.

```csharp
using System;
using VagabondK.nF.SNMP;

sealed class Values : ISnmpValueStore
{
    public int Count { get { return 1; } }
    public SnmpObject GetObject(int index)
    {
        return new SnmpObject(SnmpOid.Parse("1.3.6.1.4.1.32473.1.1.0"),
            SnmpType.Integer32);
    }
    public SnmpValue Read(int index) { return SnmpValue.Integer32(250); }
    public bool AcceptsWrite(int index, SnmpValue value) { return false; }
    public void QueueWrite(int index, SnmpValue value)
    {
        throw new InvalidOperationException("Read-only value.");
    }
}
```

`SNMP.UDP`를 함께 설치한 앱에서 네트워크 설정을 마친 뒤 다음과 같이 요청을 처리합니다.

```csharp
using System.Net;
using System.Net.Sockets;
using VagabondK.nF.SNMP;
using VagabondK.nF.SNMP.UDP;

var agent = new SnmpAgent(new Values(),
    new SnmpSystem("Evaluation agent", "sensor"), "public", "private");
using (var socket = new Socket(AddressFamily.InterNetwork,
    SocketType.Dgram, ProtocolType.Udp))
{
    socket.Bind(new IPEndPoint(IPAddress.Any, 1161));
    var session = new SnmpUdpSession(socket, agent);
    session.ProcessNext(1000);
}
```

1161은 예제 포트이며 표준 포트는 161입니다. Manager에서도 같은 포트와 community를 지정해야 합니다.
계속 서비스하려면 `ProcessNext`를 앱의 루프에서 반복 호출하십시오.
잘못된 요청의 `SnmpProtocolException`은 기록하고, 소켓 오류는 별도로 처리해야 합니다.
예제의 community는 보안 자격 증명이 아니며 평문으로 전송됩니다. 신뢰할 수 있는 망과 접근 제한이 필요합니다.
'@
    }
    '^VagabondK\.nF\.MMS\.TCP$' {
@'
### 연결된 소켓을 MMS Client에 전달하기

다음 예의 `socket`은 앱에서 연결을 마친 TCP 소켓입니다. 이 예를 사용하려면
`VagabondK.nF.MMS.Client`도 설치해야 합니다. Server 앱에서는 같은 채널을 Server 세션에 넘기며 Client 패키지는 필요하지 않습니다.

```csharp
using VagabondK.nF.MMS;
using VagabondK.nF.MMS.Client;
using VagabondK.nF.MMS.TCP;

var client = new MmsClient(new MmsSocketChannel(socket));
client.Associate();
MmsValue value = client.Read("Plant", "Temperature");
client.Release();
```

`Plant`와 `Temperature`는 상대 장치의 실제 domain·item 이름으로 바꾸십시오.
정상 종료 여부와 관계없이 소켓은 호출자가 해제해야 합니다.
전송 오류가 발생한 채널을 재사용하거나 결과가 불확실한 쓰기를 자동 재전송하지 마십시오.
'@
    }
    '^VagabondK\.nF\.IEC61850$' {
@'
### 품질을 MMS 전송 형식으로 변환하기

공통 패키지는 품질·시각 등의 형식 변환을 제공하며, 이것만으로 Client나 Server가 시작되지는 않습니다.
아래는 품질의 논리 비트를 MMS BitString에 사용할 2바이트로 기록하는 예입니다.

```csharp
using VagabondK.nF.IEC61850;

byte[] bytes = new byte[2];
Iec61850Data.WriteQuality(IecQuality.OldData | IecQuality.Questionable, bytes, 0);
```

`ReadQuality`는 13비트 MMS BitString을, `ReadDoublePoint`는 2비트 Dbpos를 해석합니다.
`ReadTimestampMilliseconds`는 MMS UtcTime을 Unix 기준 밀리초로 바꾸며 밀리초 미만은 버립니다.
시계 동기화나 값의 신뢰성 판단은 이 변환 함수가 아니라 애플리케이션의 책임입니다.
'@
    }
}
if ($example) { $documentation += "`n`n" + $example.Trim() }
if ($documentation.Length -lt 600 -or $documentation -notmatch '```(?:csharp|powershell)') {
    throw "Package documentation needs more detail or an example: $PackageId"
}
$documentation
