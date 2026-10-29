# Modbus

[전체 프로젝트](../../README.md) · [빌드·패키징·공통 검증](../../Build/README.md)

Modbus RTU/ASCII/TCP의 Client(Master)·Server(Slave) 라이브러리입니다.
공통 코어와 역할·전송별 DLL, UART 어댑터 DLL을 필요한 조합으로 선택하고,
선택 사항인 PC용 소스 생성기로 사용자 모델을 정적으로 연결할 수 있습니다.
이 문서의 명령과 코드 경로는 저장소 루트 기준입니다.

## DLL 선택과 지원 범위

| 구성 | 필요한 런타임 |
|---|---|
| RTU Master | `Modbus` + `Modbus.Client` + `Modbus.RTU` + `Modbus.Serial` |
| RTU Slave | `Modbus` + `Modbus.Server` + `Modbus.RTU` + `Modbus.Serial` |
| ASCII Master | `Modbus` + `Modbus.Client` + `Modbus.ASCII` + `Modbus.Serial` |
| ASCII Slave | `Modbus` + `Modbus.Server` + `Modbus.ASCII` + `Modbus.Serial` |
| TCP Master | `Modbus` + `Modbus.Client` + `Modbus.TCP` |
| TCP Slave | `Modbus` + `Modbus.Server` + `Modbus.TCP` |
| 사용자 정의 UART/네이티브 채널 | `Modbus.Serial` 대신 `IByteChannel` 구현 |

표의 DLL 이름에는 `VagabondK.nF.` 접두사를 붙입니다.
RTU·ASCII·TCP 모두 Master(Client)와 Slave(Server)를 지원합니다.
Client와 Server는 서로 의존하지 않으며 공통 코어는 UART·Socket에 의존하지 않습니다.
코어의 단조 시계는 `nanoFramework.Runtime.Native`의 `Environment.TickCount64`를 사용합니다.
프로젝트 의존성과 별도로 필요한 nanoFramework 시스템 어셈블리도 배포해야 합니다.
TCP 연결의 생성·수락과 UART 설정, 포트·소켓의 수명 관리는 앱의 책임이며 라이브러리는 전달받은 포트·소켓을 닫지 않습니다.

Client/Server 공통 지원 함수는 `01`, `02`, `03`, `04`, `05`, `06`, `0F`, `10`입니다.

- 주소는 **0 기반**입니다. `40001` 문서 표기는 API 주소 `0`에 해당합니다.
- Client는 호출자가 제공한 결과 배열과 재사용 내부 버퍼를 사용합니다.
  한 Client의 요청은 직렬화되지만 같은 채널을 여러 역할·인스턴스가 공유하면 안 됩니다.
- Server는 스레드를 만들지 않으며 앱이 `ProcessNextRequest()` 등을 호출합니다.
  Unit ID 하나를 처리하므로 TCP 다중 연결은 연결별 인스턴스를 앱이 관리합니다.
- 직렬 Unit ID는 `1..247`, `0`은 응답 없는 쓰기 브로드캐스트입니다.
  브로드캐스트 쓰기의 정상 반환은 송신 완료를 뜻하며, 모든 장치에 값이 적용되었음을 확인한 것은 아닙니다.
- TCP Unit ID는 `0..255`이며 TCP의 `0`은 브로드캐스트가 아닙니다.
  부분·연속 수신과 Transaction ID를 검사합니다.
- ASCII는 대문자 HEX와 CRLF를 사용하며 새 `:`에서 수신을 다시 시작합니다.
  자동 재시도는 없으며 쓰기 타임아웃이어도 상대에 이미 적용됐을 수 있습니다.

기본 최대 PDU는 253바이트, RTU ADU는 256바이트, TCP ADU는 260바이트,
ASCII ADU는 513바이트입니다. 프레임 최대 크기와 함수별 최대 수량을 각각 검사합니다.
Register는 부호 없는 16비트 값이며 숫자·문자열 해석은 아래 `RegisterConvert`를 사용합니다.

## Register 데이터 변환

공통 코어의 `RegisterConvert`는 통신 없이 `ushort[]`를 해석합니다.
별도 `VagabondK.nF.Modbus.Data` 프로젝트·DLL·NuGet은 코어에 통합했습니다.
기존 앱은 Data 참조를 제거하고 `using VagabondK.nF.Modbus;`로 바꾼 뒤 다시 빌드하십시오.
시작 위치와 개수의 단위는 레지스터입니다. 시작 위치는 `40001` 같은 주소 표기가 아닌 배열 인덱스로 지정합니다.

```csharp
using VagabondK.nF.Modbus;

ushort[] registers = new ushort[4];
RegisterConvert.WriteSingle(registers, 0, 12.5f, RegisterWordOrder.LowWordFirst);
float value = RegisterConvert.ReadSingle(registers, 0, RegisterWordOrder.LowWordFirst);
RegisterConvert.WriteAscii(registers, 0, 4, "PUMP-01");
string label = RegisterConvert.ReadAscii(registers, 0, 4);
```

`Read/WriteInt16`, `UInt16`, `Int32`, `UInt32`, `Int64`, `UInt64`, `Single`, `Double`을 제공합니다.
16비트는 Register 1개, 32비트/Single은 2개, 64비트/Double은 4개입니다.
숫자 변환은 임시 배열을 할당하지 않으며 부호 있는 정수는 2의 보수로 해석합니다.
기본값은 HighWordFirst + HighByteFirst입니다.

| 워드 순서 | 바이트 순서 | 32비트 ABCD의 레지스터 배치 |
|---|---|---|
| HighWordFirst | HighByteFirst | AB / CD |
| HighWordFirst | LowByteFirst | BA / DC |
| LowWordFirst | HighByteFirst | CD / AB |
| LowWordFirst | LowByteFirst | DC / BA |

64비트 LowWordFirst는 네 워드 전체를 역순으로 배치합니다.
이 해석 규칙은 Modbus 프레임의 Big-endian 전송 규칙을 바꾸지 않습니다.
실수는 IEEE 754 binary32/binary64이며 Infinity·NaN을 허용하되 NaN Payload 보존은 보장하지 않습니다.
공유 소스 `RealBits`를 코어에 내부 컴파일하며 DNP3 DLL에 의존하지 않습니다.

ASCII는 레지스터당 두 문자이며 바이트 순서만 적용하고 주소 순서는 유지합니다.
쓰기 시 남은 공간은 NUL로 채우며, 문자열이 필드보다 길면 잘라내지 않고 거부합니다. 읽기는 기본적으로 첫 NUL에서 멈추고 공백을 보존합니다.
`stopAtNull: false`이면 NUL도 반환합니다. ASCII 범위 밖의 문자는 읽기·쓰기 모두 거절하고
읽기 시 첫 NUL 뒤를 포함한 필드 전체를 검증합니다. 쓰기는 모든 인수를 검증한 후에만 배열을 변경합니다.
빈 문자열·길이 0 필드도 허용하며 문자열 읽기는 결과 문자열·문자 배열을 할당합니다.
배열 동시 접근은 호출자가 동기화해야 합니다.

통합 당시 Release PE는 4,584 B로 이전 코어+Data 합계 4,840 B보다 256 B 작았고 DLL은 10,752 B였습니다.
이는 과거 빌드 산출물 비교이며 현재 빌드 크기나 실행 RAM 실측치가 아닙니다.

## 빌드 시점 객체 매핑

`VagabondK.nF.Modbus.Mapping`은 앱의 `partial class`에 직접 접근하는 C# 7.3 코드를 생성합니다.
리플렉션·이름 기반 Dictionary·박싱·Point 객체·델리게이트 기반 런타임 바인딩을 사용하지 않습니다.
Modbus 전용이며 기존 `VagabondK.Interface`와의 API 호환이나 프로토콜 간 자동 동기화는 제공하지 않습니다.

```csharp
using VagabondK.nF.Modbus.Mapping;

[ModbusMap(ModbusMapRole.Client, MaxReadRegisters = 16, MaxReadBits = 64)]
public partial class Pump
{
    [InputRegister(100,
        WordOrder = ModbusWordOrder.LowWordFirst)]
    public float Temperature { get; private set; }

    [HoldingRegister(200, Writable = true)]
    public int Setpoint { get; set; }
}
```

`[Coil(address)]`, `[DiscreteInput(address)]`, `[HoldingRegister(address)]`,
`[InputRegister(address)]`로 영역을 바로 표현할 수 있습니다.
기존 `[ModbusPoint(ModbusArea.영역, address)]`도 그대로 지원하며,
동일한 옵션이면 버퍼·요청 묶음·멤버 접근을 포함한 실행 코드를 동일하게 생성합니다.
`Writable`, `ReadGroup`, `ByteOrder`, `WordOrder`의 의미와 기본값은 바뀌지 않습니다.
한 멤버에 축약형과 기존형을 함께 붙이거나 서로 다른 영역 특성을 함께 붙이면 빌드 오류입니다.

Client는 `ReadModbus(client, unitId)`와 Writable 멤버별 `WriteModbus<멤버명>`을 생성합니다.
속성의 setter 호출만으로 송신하지 않습니다. 빈틈없이 인접한 주소만 묶고 레지스터 125개·비트 2000개 상한을 지키며
다중 Register 값의 중간에서 요청을 나누지 않습니다. 모델별 수신 배열은 최대 요청 크기로 한 번 만들어 재사용합니다.
읽기·쓰기·모델 접근 전체는 앱이 직렬화해야 하며 Client가 각 요청에 적용하는 잠금만으로는 충분하지 않습니다.
오류는 그대로 전달하며, 앞서 성공한 요청이나 setter로 반영한 값은 되돌리지 않습니다.

### 읽기 그룹과 버퍼 예산

Client Point의 `ReadGroup = "Status"`는 `ReadModbusGroupStatus(client, unitId)`를 추가합니다.
기존 `ReadModbus`는 그룹과 무관하게 모든 Point를 같은 순서·분할로 읽습니다.
그룹 이름은 대소문자를 구분하고 영문자로 시작하는 영문자·숫자·밑줄만 허용합니다.
그룹 이름을 지정하지 않거나 빈 문자열로 지정한 Point는 전체 읽기에만 포함됩니다. Server에 그룹을 지정하면 빌드 오류가 발생합니다.

그룹 메서드는 해당 그룹의 값만 갱신합니다. 다른 그룹이나 빈 주소를 사이에 둔 구간을 하나의 요청으로 묶지 않으며, 다중 레지스터 값도 분할하지 않습니다.
전체 읽기의 요청 경계를 유지하고 같은 배열을 재사용하므로 그룹 추가로 수신 배열이 늘지 않지만 요청은 더 나뉠 수 있습니다.
백그라운드 스레드나 타이머를 만들지 않으며 읽기 주기도 자동으로 관리하지 않습니다. 그룹 메서드가 추가되는 만큼 플래시 사용량은 늘어납니다.

```csharp
// Point에 ReadGroup = "Status"를 지정한 경우
// pump.ReadModbusGroupStatus(client, unitId);
// pump.ReadModbus(client, unitId); // 전체 갱신
```

`MaxReadRegisters`는 1~125(기본 125), `MaxReadBits`는 1~2000(기본 2000)입니다.
배열은 옵션 상한 자체가 아니라 실제 생성 요청의 최대 크기로 잡습니다.
상한을 줄이면 요청 수가 늘 수 있고 한 값의 폭보다 작은 Register 상한은 빌드 오류입니다.
Server에는 이 읽기 상한 옵션을 지정하지 않습니다.

`BufferBudgetBytes`는 두 역할 모두에서 지정할 수 있으며, 기본값 0이면 예산을 검사하지 않습니다.
생성 코드 머리말에는 Point 수·요청 수·배열 길이와 배열 원소 크기의 논리적 합계(바이트)를 기록합니다.
예산을 초과하면 **VKNF006 경고**가 발생하며, `/warnaserror`를 적용한 빌드는 실패합니다.
`ushort`는 2바이트, `bool`은 1바이트로 계산하며 객체 헤더·메모리 정렬·모델·잠금 객체·Client/Server·전송 버퍼는 제외합니다.
실제 힙 사용량을 보장하는 수치는 아니며, 주석으로만 기록하므로 보고 자체에 런타임 비용은 들지 않습니다.

### Server 저장소와 지원 형식

`[ModbusMap(ModbusMapRole.Server)]`는 중첩 `ModbusStore : IModbusDataStore`를 생성합니다.
`new Pump.ModbusStore(pump, syncRoot)`를 `ModbusServer`에 전달하고 앱도 같은 `syncRoot`로 모델을 잠급니다.
전체 범위 검증 후 접근하며 빈 주소·읽기 전용 쓰기·다중 Register 값의 부분 쓰기는 `IllegalDataAddress`로 거절합니다.
값의 일부 Register 읽기는 허용합니다.

생성 저장소는 `IModbusDataStoreValidator`도 구현합니다. Server가 고정 주소 오류를 미리 검사하여
관리 예외 생성 없이 표준 예외 응답을 보내므로 nanoCLR의 예외 진단 지연을 피할 수 있습니다.
기존 사용자 정의 저장소의 예외 변환은 유지하며, 저장소를 직접 호출할 때 잘못된 범위를 지정하면 여전히 예외가 발생합니다.
요청을 다른 저장소로 전달하거나 여러 저장소를 결합하는 구현은 Validator 검사에도 같은 주소 규칙을 적용해야 합니다.

주소 검증은 생성된 연속 구간을 사용하며 매 Point마다 처음부터 탐색하지 않습니다.
쓰기 디코딩은 해당 값에만 지역 변수를 사용하고 읽기도 지역 변수를 재사용합니다.
Release 읽기·쓰기 지역 변수 슬롯 수가 Point 수와 함께 증가하지 않는지 회귀 검사합니다.
이는 스냅샷이나 트랜잭션을 제공한다는 뜻이 아닙니다. setter의 부작용이나 예외가 발생해도 앞선 변경은 되돌리지 않습니다.
호출 중 앱·Setter는 입력 배열을 변경하거나 저장소에 재진입하면 안 됩니다.

| 항목 | 범위 |
|---|---|
| 모델 | 최상위·비제네릭·비상속 `partial class`, 인스턴스 필드·일반 속성, private setter |
| Coil/DiscreteInput | `bool` |
| Holding/Input Register | `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double` |
| 주소 | 0~65535, 바이트·워드 순서 지정, 같은 영역의 주소 중첩은 빌드 오류 |
| 쓰기 | `Writable` 기본 false, Coil/HoldingRegister만 허용 |
| 접근 | Client는 대입 가능해야 하며 Server 읽기 전용은 Getter만 있거나 readonly 필드여도 됨 |
| 미지원 | 문자열·배열·enum·Register 내부 Bit·자동 변경 알림·주기·재접속·재시도 |

모델이 커지면 직접 접근 코드·요청 수도 증가하므로 Flash/RAM 예산을 확인하십시오.
숫자·bool 매핑 모두 공통 코어를 사용합니다. 생성된 매핑 Attribute 타입에는
`NANOFRAMEWORK` 또는 `NANOFRAMEWORK_1_0` 빌드에서 `ExcludeType`을 붙여 MCU PE에서 제외합니다.
이는 PC 컴파일 시 선언을 해석하는 용도이며 런타임에서 Attribute를 조회하지 않습니다.
해당 빌드 상수가 없는 일반 .NET 컴파일에서는 선언 타입이 남습니다.
Generator·Roslyn DLL은 PC 빌드 전용이며 MCU에 배포하지 않습니다.

### 생성기 연결과 패키지 검증

PC에서 .NET SDK·Roslyn 4.12 이상을 사용합니다. `.nfproj`에서 직접 참조할 때는
Generator의 `dotnet restore`를 먼저 수행하고 Analyzer 메타데이터를 지정합니다.
아래 경로는 라이브러리를 사용하는 프로젝트의 위치에 맞게 조정하십시오.

```xml
<ProjectReference Include="..\VagabondK.nF.Modbus.Mapping\VagabondK.nF.Modbus.Mapping.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false"
                  SkipGetTargetFrameworkProperties="true" />
```

NuGet Analyzer는 `analyzers\dotnet\cs`에만 들어갑니다.
`packages.config`를 사용하는 프로젝트에서는 `build\VagabondK.nF.Modbus.Mapping.targets`의 Import를 확인하고,
SDK의 PackageReference를 사용하는 프로젝트에서는 자동으로 추가되는 Analyzer를 사용합니다.
IDE와 빌드는 같은 Analyzer를 사용하지만 Visual Studio GUI IntelliSense는 별도 수동 확인 대상입니다.

```powershell
dotnet run -c Release --project .\Tests\Modbus\VagabondK.nF.Modbus.Mapping.Tests
.\Build\Pack-ModbusMapping.ps1
.\Build\Test-ModbusMapping.ps1
.\Build\Test-ModbusMappingScaling.ps1
```

패키지 시험은 역할(Client/Server)과 매핑 형식(숫자/bool 전용)을 조합한 네 사용 앱을 NuGet만으로 복원·빌드하고,
불필요한 역할별 DLL이나 PC용 DLL이 포함되었는지 검사합니다. 규모 확장 시험은 8/32/128개 매핑의 PE·배포 이미지 크기를 확인합니다.
PC IL·지역 변수·할당·실행 시간은 MCU 성능이 아닙니다.
실물 Heap·처리 시간·독립 `pymodbus` 상호운용은
`Build\Test-ModbusMappingHardware.ps1 -AllowDeviceChanges`로 확인합니다.
`-NoDebugger -StabilitySeconds 120 -SlaveRuns 2`로 반복 복구를 검사할 수 있습니다.
[실물 결과·복원 절차](../../Tests/Modbus/VagabondK.nF.Modbus.Mapping.Hardware/README.md)를 참조하십시오.
Stack 사용량과 호출 중 일시 할당량은 측정하지 않았습니다.

## 사용 예

### RTU Master

```csharp
using System.IO.Ports;
using VagabondK.nF.Modbus.Client;
using VagabondK.nF.Modbus.RTU;
using VagabondK.nF.Modbus.Serial;

// 먼저 보드에 맞는 RX/TX/RTS 핀 매핑을 수행합니다.
using (var port = new SerialPort("COM2", 9600, Parity.Even, 8, StopBits.One))
{
    port.Mode = SerialMode.RS485;
    port.Open();
    var transport = new RtuTransport(new SerialPortChannel(port),
        baudRate: 9600, receiveMode: RtuReceiveMode.ResponseLength,
        bitsPerCharacter: 11, timeoutMilliseconds: 1000);
    var client = new ModbusClient(transport);
    var registers = new ushort[4];
    client.ReadHoldingRegisters(1, 0, 4, registers, 0);
    client.WriteSingleRegister(1, 10, 123);
}
```

`bitsPerCharacter`는 시작·데이터·패리티·정지 비트를 모두 포함하며 8E1/8N2는 11비트입니다.
RS-485 방향 제어는 보드 펌웨어·배선에 의존하며 자체 제어가 필요하면 사용자 채널로 구현합니다.
`RequestLength`는 Server 요청, `ResponseLength`는 Client 응답 수신에 사용합니다.
알려진 함수의 길이·CRC·상한을 검사하지만 정확한 t1.5/t3.5 검증을 대신하지는 않습니다.
알 수 없는 함수는 무통신 간격 방식이며 기존 생성자의 기본값은 `SilentInterval`입니다.
ASCII는 상대의 설정(일반적으로 7E1)에 맞추고 `AsciiTransport(new SerialPortChannel(port), 1000)`을 사용합니다.

### TCP Slave

```csharp
using System.Net;
using System.Net.Sockets;
using VagabondK.nF.Modbus.Server;
using VagabondK.nF.Modbus.TCP;

var store = new ArrayDataStore(64, 64, 128, 128);
store.SetInputRegisters(0, 1, new ushort[] { 42 });
using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
{
    listener.Bind(new IPEndPoint(IPAddress.Any, 502));
    listener.Listen(1);
    using (var connection = listener.Accept())
    {
        var server = new ModbusServer(1,
            new TcpTransport(new SocketChannel(connection), 1000), store);
        server.ProcessNextRequest();
    }
}
```

연결 하나의 요청 하나만 처리하는 예입니다. 실제 앱은 연결 수 제한·반복·오류 기록·종료·재접속을 구현해야 합니다.
TCP Master는 연결한 소켓의 `SocketChannel`·`TcpTransport`를 `ModbusClient`에 전달합니다.

## 저장소·오류·상태 기반 수신

`IModbusDataStore`로 실제 I/O를 연결할 때는 전체 주소 검증과 앱 스캔 루프 동기화가 필요합니다.
다중 쓰기 중간 실패와 실제 장치 명령의 원자성은 구현 책임입니다.
기본 `ArrayDataStore`는 범위를 검증한 후 잠금 안에서 복사합니다.
Coil/Holding Register는 쓰기 메서드, Discrete Input/Input Register는 `Set` 메서드로 갱신합니다.
사용하지 않는 영역의 크기는 0으로 둘 수 있으며, 여러 메서드 호출에 걸쳐 일관된 스냅샷을 제공하지는 않습니다.

| 오류 | 의미 |
|---|---|
| `ArgumentException` 계열 | 잘못된 로컬 호출 인수 |
| `TimeoutException` | 송수신 기한 만료 |
| `ModbusProtocolException` | CRC/LRC·길이·응답 불일치 또는 TCP 종료 |
| `ModbusException` | 상대 예외 응답 또는 저장소의 Modbus 오류 |
| UART·Socket 예외 | 하위 I/O 오류를 원래대로 전파 |

저장소의 `ModbusException`은 표준 예외 응답으로 변환하지만 예상하지 못한 앱 예외는 전파합니다.
응답할 수 없는 브로드캐스트의 저장소 오류도 앱에 전파합니다.
TCP 오류 후에는 기존 연결을 폐기하십시오. 직렬은 늦은 응답·잔여 바이트를 정리하는 정책이 필요하며
자동 재동기화·쓰기 재전송은 제공하지 않습니다.
기존 예외 기반 API는 한 바이트도 받지 못한 경우와 일부만 받은 경우의 시간 초과를 구분하지 않습니다. 따라서 TCP Server에서는 시간 초과 후 연결을 폐기하는 보수적인 정책을 사용합니다.

반복 직렬 Server는 `RtuTransport.TryReceive`·`AsciiTransport.TryReceive` 또는
`ModbusServer.TryProcessNextRequest`를 사용할 수 있습니다.
RTU/ASCII는 `IModbusPollingTransport`를 구현하지만 TCP는 아직 구현하지 않습니다.

| `ReceiveStatus` | 의미 |
|---|---|
| `Success` | 유효한 프레임 수신 |
| `Idle` | 제한 시간 동안 수신한 바이트 없음 |
| `Timeout` | 부분 수신·잡음 뒤 전체 기한 만료 |
| `InvalidLength` / `FrameTooLong` | 잘못된 길이 / 최대 길이 초과 |
| `CrcMismatch` / `InvalidUnitId` | CRC / 직렬 Unit 범위 오류 |
| `LrcMismatch` / `InvalidCharacter` | LRC / 대문자 HEX가 아닌 문자 |

수신 실패는 예외를 발생시킨 뒤 잡는 대신 상태로 직접 반환합니다. 이때 `length=0`, `context=default`이며 출력 PDU는 갱신하지 않습니다.
실제 I/O·인수·채널 반환 수량 오류는 예외로 전파합니다.
`RtuFrame.TryDecode`·`AsciiFrame.TryDecode`·`OperationDeadline.TryGetRemainingMilliseconds`도 상태 검사를 제공합니다.
처리의 `Success`에는 다른 Unit·읽기 브로드캐스트 등 응답 없는 유효 프레임도 포함됩니다.
상태 기반 수신을 지원하지 않는 전송에서는 `NotSupportedException`이 발생합니다. 저장소 접근·요청 처리·송신에 관한 기존 예외 계약은 유지합니다.
Server의 모든 Modbus 예외 경로를 제거한 것은 아닙니다.

## 직렬 통신의 알려진 제약

UART 어댑터는 `BytesToRead`만큼만 읽으며, 버퍼가 비어 있으면 1ms씩 대기하며 수신 여부를 확인합니다.
일부 드라이버가 요청 수량 전체를 기다리므로 최대 프레임 길이를 그대로 요구하지 않습니다.
`SilentInterval`은 수신 뒤 무통신을 밀리초 단위로 추정하는 관리 코드 구현입니다.
19,200bps 초과는 2ms, 이하는 3.5문자 시간을 올림하며 정확한 t1.5/t3.5 감지·위반 검사가 아닙니다.
스케줄링·UART 버퍼링으로 프레임이 합쳐지거나 나뉠 수 있습니다.

Geekble Nano의 시험 펌웨어에서 120바이트 요청 성공·121바이트 실패와
255바이트의 120+120+15 전달, 첫 두 배치 사이 약 140ms를 관측했습니다.
ESP-IDF FIFO full 기본 임계값 120바이트와 부합하지만 네이티브 펌웨어는 변경하지 않았습니다.
해당 조합의 `SilentInterval`은 최대 길이 RTU 요청을 처리하지 못했습니다.
길이 기반 모드는 전체 기한 안의 긴 내부 전달 간격을 허용하고 첫 프레임 길이만 소비하는 대안이지 시간 규격 준수 보장이 아닙니다.
실제 버스의 무통신 상태 확인과 오류 후 재동기화는 앱·채널의 책임이며, 정밀한 프레임 경계 판정이 필요하면 네이티브 구현을 사용하는 `IModbusTransport`로 교체할 수 있습니다.

ASCII는 시작 후 문자 사이 최대 1초와 전체 기한을 함께 적용하며 새 `:`도 전체 기한을 갱신하지 않습니다.
기존 `Receive`는 `Idle`과 `Timeout` 모두 `TimeoutException`으로 변환합니다.
느린 장치에는 충분한 전체 기한을 지정하십시오.
Modbus는 인증·암호화가 없으므로 신뢰할 수 있는 제어망과 접근 제한이 필요합니다.

## 샘플과 검증

| 샘플 | 내용 |
|---|---|
| [TCPServer](../../Samples/Modbus/VagabondK.nF.Modbus.TCPServer/README.md) | ESP32-S3 Wi-Fi Slave와 PC 시험 |
| [TCPClient](../../Samples/Modbus/VagabondK.nF.Modbus.TCPClient/README.md) | Master와 PC 오류 주입 Server |
| [SelfTest](../../Samples/Modbus/VagabondK.nF.Modbus.SelfTest/README.md) | MCU 메모리 채널 RTU/ASCII 시험 |
| [SerialServer](../../Samples/Modbus/VagabondK.nF.Modbus.SerialServer/README.md) | GPIO17/18·RS-485 Slave, 상태 수신·복구·진단 카운터 |
| [SerialClient](../../Samples/Modbus/VagabondK.nF.Modbus.SerialClient/README.md) | MCU Master·PC COM6 Slave, 분할 응답·오류·Heap 시험 |
| [Mapping](../../Samples/Modbus/VagabondK.nF.Modbus.Mapping/README.md) | 정적 모델의 실제 TCP 양방향 사용 |

매핑 샘플은 기본 Slave이며 `MAPPING_MASTER` 정의 시 Master입니다.
SelfTest는 UART/RS-485 검증을 대체하지 않으며 각 샘플은 보드에 하나씩 배포합니다.

```powershell
dotnet run -c Release --project .\Tests\Modbus\VagabondK.nF.Modbus.Tests
dotnet run -c Release --project .\Tests\Modbus\VagabondK.nF.Modbus.Data.Tests
```

PC 시험은 CRC/LRC·최대/부분/연속 프레임·8개 함수·주소/수량·브로드캐스트·저장소와 TCP 루프백을 검사합니다.
실제 소켓의 MBAP 중간 EOF·느린 분할·길이/Transaction ID·Peer 종료도 포함합니다.
[장애·복구 검증표](../../Tests/FaultRecovery.md)는 PC/nanoCLR/실물 범위를 구분합니다.

### 과거 MCU 검증 기록

Geekble Nano (`ESP32_S3_QUAD` 1.18.0.30)에서 USB·Wi-Fi와 외부 RS-485 모듈·컨버터를 사용했습니다.
아래는 해당 시험의 관측치이며 현재 코드의 모든 환경 보장이 아닙니다. 재실행·배선은 각 샘플 문서를 따르십시오.

| 시험 | 확인한 범위 |
|---|---|
| TCP Server | 10,159개 검사, 최대 Register 읽기 10,000회, 재접속 20회, MBAP·Timeout 복구 |
| TCP Client | 8개 함수·최대 수량, 반복 10,000회, 오류 주입 4종과 새 연결 복구 |
| MCU 메모리 채널 | 10,291개 검사, RTU/ASCII 각각 최대 읽기 1,000회·CRC/LRC·브로드캐스트 |
| RTU Server | 디버거 없이 8개 함수·119~256바이트 경계·최대 읽기 100회·12초 유휴/부분 수신·CRC 복구 |
| RTU Client | 최대 읽기 500회·119~255바이트 응답·브로드캐스트 4종·오류/지연 6종 후 복구 |
| ASCII Server | 9600 7E1·8개 함수·최대 513바이트 비정상 요청·최대 읽기 100회·LRC/HEX/CRLF/기한 복구 |
| ASCII Client | 9600 7E1·최대 읽기 500회·브로드캐스트 4종·오류 등 13단계·513바이트 응답 |

디버거 없는 예외의 USB 출력 지연을 대조했습니다. COM3 출력만 읽으면 오류 후 약 46~62ms에 응답했고
COM3를 닫으면 5초 무응답 후 복귀했습니다. 상태 수신 적용 뒤 닫힌 COM3에서도 정상·유휴·CRC 뒤 약 61~72ms였습니다.
당시 PC 회귀 12,667개 검사와 신규 상태/유휴 경로의 첫 예외 이벤트 0회를 확인했습니다.
SerialServer는 `Idle`에 버퍼를 버리지 않고 오류에만 재동기화하며 Input Register 120..125에 진단을 노출합니다.

TCP의 두 역할 모두에서 GC 후 사용 가능한 힙 크기는 역할별 5개 관측 시점에 걸쳐 동일했습니다.
RTU Client는 100~500회 5개 시점에서 759,264 B, ASCII Client는 758,772 B로 동일했습니다.
RTU 메모리 시험은 전후 432 B 감소했고 ASCII는 변화가 없었습니다.
RTU Server는 실물 2시간 최대 읽기 11,901회와 오류/유휴 복구 각 91회를 통과했습니다.
짧은 관측은 누수 부재 보장이 아니며 전원 장애·정밀 타이밍·다른 구성의 장기 검증은 남아 있습니다.

실행 `.exe`·`.pe`만 있고 배포 `.bin`이 없으면 `.nfproj`를 Release Rebuild하십시오.
어셈블리 목록만으로 성공을 판단하지 말고 완료 마커·실제 응답을 확인해야 합니다.
현재 배선은 GPIO17/18·레벨 변환기·DM2472·PC COM6 CS-428이며 종단저항 없이 시험했습니다.
다른 구성은 3.3V 로직 호환 RS-485 트랜시버·USB-RS485 어댑터와 핀맵·종단·바이어스·공통 전위를 확인하십시오.
5V 신호를 ESP32 GPIO에 직접 연결하지 말고 USB용 GPIO19/20은 UART 후보에서 제외하십시오.
정밀 프레임 간격·DE/RE 검증에는 로직 애널라이저를 권장합니다.

## 참고 자료와 구현 방침

기존 Modbus 패키지에 대한 런타임 의존성 없이 독립 구현합니다.
참고 프로젝트를 산업 적합성·규격 검증의 대체재로 간주하지 않습니다.

- [Modbus 공식 규격](https://www.modbus.org/specs.php): Application Protocol·Serial Line·TCP/IP
- [nanoFramework Modbus](https://github.com/nanoframework/nanoFramework.IoT.Device/tree/main/devices/Modbus): 기존 API·UART/RS-485 참고
- [SerialPort](https://github.com/nanoframework/System.IO.Ports) · [Socket](https://github.com/nanoframework/System.Net): 하드웨어 I/O

기존 Modbus 구현 코드를 직접 복사하지 않았으며 향후 직접 재사용 시 출처·라이선스 고지를 보존합니다.
