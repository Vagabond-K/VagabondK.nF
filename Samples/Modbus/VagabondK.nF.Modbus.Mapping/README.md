# Modbus 정적 매핑 TCP 샘플

실제 nanoFramework MCU의 네트워크와 TCP Socket을 사용하는 샘플입니다.
센서/GPIO 제어가 아닌 통신·객체 매핑 예제이며 Temperature 등의 초기값은 데모 데이터입니다.
이 샘플 앱 자체의 실물 실행과 별도로, 동일 Generator·통신 라이브러리의
[전용 실물 시험기](../../../Tests/Modbus/VagabondK.nF.Modbus.Mapping.Hardware/README.md)에서 Master·Slave 양방향을 검증했습니다.

## 장치 준비

`AllFormats.cs`는 Boolean과 8종 Register 형식을 모두 Client/Server 모델에 선언합니다.
시작 시 네트워크 연결 전에 메모리 PDU 루프백으로 전체 읽기·명시적 쓰기를 검사하며,
외부 Slave/실제 장치에 이 예비 쓰기를 보내지 않습니다. 기존 TCP 주소 표는 바꾸지 않았습니다.
전체 형식·역할은 [대조표](../../MappingCoverage.md)를 참조하십시오.

Device Explorer에서 보드의 Wi-Fi SSID·Password·DHCP·AutoConnect를 설정합니다.
소스에 자격 증명을 기록하지 않습니다. 네트워크 주소를 얻으면 Debug Output에 IP를 출력합니다.
신뢰할 수 있는 테스트 LAN에서만 실행하십시오. Modbus TCP에는 인증·암호화가 없으며,
포트를 외부에 노출하거나 실제 설비를 쓰기 시험 대상으로 사용하지 마십시오.

## Slave/Server: 기본 동작

`Program.cs`에서 `MAPPING_MASTER`를 정의하지 않으면 TCP 15020, Unit ID 1로 수신합니다.
PC의 Modbus Master 도구 또는 다른 MCU에서 보드 IP로 접속하십시오.
한 번에 연결 하나만 처리하고, 통신 오류나 30초 수신 시간 초과 시 연결을 닫은 뒤 새 연결을 기다립니다.
수신 소켓 자체의 Bind/Accept 오류는 숨기지 않고 앱 오류로 전달합니다.

| 영역 | 0 기반 주소 | 형식 | 초기값 | 원격 쓰기 |
|---|---|---|---|---|
| Coil | 10 | bool | false | 가능 |
| HoldingRegister | 200~201 | int32, HighWordFirst | 1200 | 가능 |
| InputRegister | 100~101 | float32, LowWordFirst | 12.5 | 불가 |
| InputRegister | 102 | int16 | -1 | 불가 |

바이트 순서는 모두 HighByteFirst입니다. 예를 들어 InputRegister 100~102를 읽으면
`0000 4148 FFFF`가 반환됩니다. HoldingRegister 200~201의 초기값은 `0000 04B0`입니다.
int32 쓰기는 반드시 Register 두 개를 한 요청으로 보내야 하며 부분 쓰기는 IllegalDataAddress로 거절합니다.
모델을 앱에서 변경할 때도 저장소에 전달한 `sync` 객체로 잠가야 합니다.

## Master/Client

`Program.cs` 맨 위의 `// #define MAPPING_MASTER`를 `#define MAPPING_MASTER`로 바꾸고,
`ServerAddress`를 테스트 Slave의 실제 IP로 설정한 뒤 다시 빌드·배포합니다.
Slave는 위와 같은 Register Map을 제공해야 합니다. 포트는 기본 15020입니다.
PC의 테스트 Slave를 사용할 경우 방화벽에서 해당 포트를 허용하십시오.

상태 그룹(`Temperature`, `Status`, `Enabled`)을 매 반복 읽고,
설정 그룹(`Setpoint`)은 최초와 이후 10번의 반복마다 읽습니다.
반복 사이에 1초를 대기하므로 정확한 실시간 주기가 아니라 통신 시간에 더해지는 대기입니다.
`ReadGroup = "Status"|"Settings"` 선언에서 `ReadModbusGroupStatus`·`ReadModbusGroupSettings`가 생성됩니다.
기존 `ReadModbus`를 호출하면 여전히 모든 값을 읽습니다.
그룹은 같은 수신 버퍼를 재사용하며 타이머나 추가 스레드를 생성하지 않습니다.
`MaxReadRegisters = 2`이므로 InputRegister 100~101과 102는 서로 다른 요청입니다.
오류 시 연결을 해제하고 멈추며 자동 재접속·자동 재시도하지 않습니다.
기본은 읽기 전용입니다. 명시적 쓰기 사용 예는 `RunMaster` 안의 주석으로만 제공하며,
전용 테스트 Slave에서만 활성화하십시오. Setter 대입만으로는 송신하지 않습니다.

## 빌드와 PC 회귀 검증

Visual Studio의 nanoFramework 환경에서 프로젝트를 빌드하고 Device Explorer로 배포합니다.
Master와 Slave를 동시에 실행하려면 MCU 두 대 또는 MCU 한 대와 PC 도구가 필요합니다.
이 샘플은 보드의 Wi-Fi 설정을 코드로 변경하지 않습니다.

```powershell
dotnet run -c Release --project .\Tests\Modbus\VagabondK.nF.Modbus.Mapping.Tests\VagabondK.nF.Modbus.Mapping.Tests.csproj
```

PC 테스트는 `Models.cs`를 공유하지만 Socket 샘플 앱은 실행하지 않습니다.
샘플의 `AllFormats.cs`·`SampleLoopback.cs`는 MCU 시작 시 실행하는 메모리 PDU 예비 검사이며,
`Tests`의 별도 `LoopbackSample.cs`는 PC 회귀용입니다. 어느 메모리 검사도 실제 TCP/UART 통신을 검증하지 않습니다.
