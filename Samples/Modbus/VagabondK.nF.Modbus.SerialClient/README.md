# MCU Serial Client / PC Server 시험기

Geekble Nano가 Master, PC COM6의 시험기가 Unit 1 Slave로 동작합니다.
기존 SerialServer와 같은 GPIO17 TX / GPIO18 RX, 9600 8E1,
DM2472 자동 방향 RS-485 배선을 사용합니다. COM3는 앱 배포에만 사용하고
시험 도중 디버거나 USB 출력 수신기를 연결하지 않습니다.
네이티브 펌웨어와 배선을 바꾸지 않습니다.

**독립된 시험 버스 전용입니다. 실제 설비에 연결하면 안 됩니다.**
RTU/ASCII 모두 MCU는 자동으로 쓰기/브로드캐스트를 수행합니다. PC 시험기는 새 메모리
저장소를 사용하므로 이전 MCU Server의 데이터를 대상으로 하지 않습니다.
동일 버스에 다른 Master를 연결하거나 PC SerialServer 시험을 동시에 실행하면
안 됩니다. 시험 앱 배포는 기존 MCU Server 앱을 교체합니다.

## 실행

저장소 루트에서 PC 시험기를 먼저 실행해 `READY`를 확인합니다.
.NET 10 SDK와 NuGet `System.IO.Ports`가 필요합니다.

```powershell
dotnet run --project .\Tests\Modbus\VagabondK.nF.Modbus.SerialClientFixture\VagabondK.nF.Modbus.SerialClientFixture.csproj -c Release -- COM6
```

별도 터미널에서 MCU 앱을 빌드하고 배포합니다.

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' .\Samples\Modbus\VagabondK.nF.Modbus.SerialClient\VagabondK.nF.Modbus.SerialClient.nfproj /t:Rebuild /p:Configuration=Release /v:minimal /nologo
nanoff --nanodevice --deploy --serialport COM3 --image .\Samples\Modbus\VagabondK.nF.Modbus.SerialClient\bin\Release\VagabondK.nF.Modbus.SerialClient.bin --suppressnanoffversioncheck
```

MCU는 시작 후 10초 기다린 뒤 시험하고, 완료하면 다음 부팅까지 대기합니다.
PC의 `PASS`와 종료 코드 0으로 판정합니다. PC는 15분 내 완료 마커가 없거나
요청 CRC/길이 오류, 시험 단계 누락이 있으면 실패합니다.
시험기만 다시 실행해서는 MCU가 재시작되지 않습니다.
재시험에는 새 PC 저장소와 MCU 앱 재시작이 모두 필요합니다.

## 범위와 판정

- 8개 함수, 최대 1968 Coil / 123 Register 쓰기와 되읽기,
  2000 Coil/Discrete Input 및 125 Input Register 읽기.
- Register 응답 ADU 119/121/123/239/241/243/255바이트와
  Coil 응답 ADU 120/240바이트 경계.
  MCU는 `RtuReceiveMode.ResponseLength`를 사용해 UART 관리 버퍼 배치 사이의
  공백으로 긴 응답을 자르지 않습니다.
- 최대 Register 읽기 500회 데이터 비교, 별도 워밍업 10회와 경계 읽기.
  GC 후 가용 힙을 기준점과 100회마다 RS-485로 보고합니다.
- 브로드캐스트 쓰기 4종 후 되읽기. PC는 브로드캐스트에 응답하지 않습니다.
- 주소 60000..60005에서 순서대로 예외 02, 무응답, CRC 오류,
  3바이트 부분 응답, 500ms 지연 정상 응답, 2500ms 지연 늦은 응답을 주입합니다.
  MCU 수신 제한은 1500ms입니다.
- 각 오류/지연 처리 후 MCU가 Input Register 100을 읽어 확인하고 복구 마커를
  전송해야 다음 단계로 진행합니다. 완료 마커만 보내서는 통과하지 않습니다.

현재 Client API는 예외 기반입니다. 예상 오류를 catch하더라도 CLR의 USB 예외
출력이 독립 실행에서 추가 지연을 만들 수 있으므로 이 시험은 오류 후의
실시간 응답 상한을 보장하지 않습니다. PC의 15분 제한은 전체 시험 제한입니다.
늦은 RTU 응답에는 트랜잭션 ID가 없으므로 오류 후 즉시 다음 요청을 보내지 않고
시험기 지연보다 긴 3초를 기다린 뒤 100ms 동안 무통신이 관측될 때까지 잔여 데이터를 비웁니다.
이는 알려진 지연을 주입하는 시험 정책이며 임의 장치의 안전한 재시도 보장이 아닙니다.

종단저항 없는 현재 구성의 기능 시험이며 정밀 t1.5/t3.5, 전기적 품질,
장기 안정성, 메모리 누수 부재를 보증하지 않습니다.

## ASCII 선택

동일 프로젝트를 `/p:SerialProtocol=Ascii`로 Rebuild하고 PC 시험기에
`COM6 Ascii`를 전달합니다. MCU/PC 모두 9600 7E1을 사용합니다.

```powershell
dotnet run --project .\Tests\Modbus\VagabondK.nF.Modbus.SerialClientFixture\VagabondK.nF.Modbus.SerialClientFixture.csproj -c Release -- COM6 Ascii
# 별도 터미널에서 빌드/배포
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' .\Samples\Modbus\VagabondK.nF.Modbus.SerialClient\VagabondK.nF.Modbus.SerialClient.nfproj /t:Rebuild /p:Configuration=Release /p:SerialProtocol=Ascii /v:minimal /nologo
nanoff --nanodevice --deploy --serialport COM3 --image .\Samples\Modbus\VagabondK.nF.Modbus.SerialClient\bin\Release\VagabondK.nF.Modbus.SerialClient.bin --suppressnanoffversioncheck
```

RTU와 같은 최대 수량/반복/브로드캐스트/힙 보고를 실행합니다.
ASCII의 정상 최대 읽기/쓰기 ADU는 511바이트이며, PDU 253바이트를 담는
최대 513바이트 ADU는 응답의 바이트 수 필드가 잘못된 시나리오로 별도 검사합니다.
RTU용 수량 경계에서 ASCII 응답 길이는
239/241/243/247/479/481/483/487/511바이트입니다.

주소 60000..60012의 13단계는 예외 02, 무응답, LRC 오류, 부분 응답,
500ms 정상 지연, 4000ms 늦은 응답, 잘못된 HEX, 잘못된 CRLF,
513바이트 초과(514바이트), 문자 간 1400ms 만료, 300ms 허용 지연,
새 `:`에 의한 재시작, 유효 LRC의 513바이트 잘못된 응답입니다.
전체 제한 시간은 3초, 프레임 시작 뒤 문자 사이 제한은 1초입니다.
각 단계 뒤 4초 대기/잔여 버퍼 정리와 정상 읽기/복구 마커를 요구합니다.
500회 반복 구간의 힙 관측은 오류 주입 전에 수행하므로 오류 경로의 할당까지
포함하는 메모리 시험은 아닙니다.

2026-10-23 COM3/디버거를 닫은 ASCII 실물 시험에서 위 13단계와
최대 읽기 500회, 브로드캐스트 4종을 모두 통과했습니다.
PC READY부터 완료 마커까지 465.2초였고, GC 후 가용 힙은 기준점과
100/200/300/400/500회 관측에서 모두 758,772바이트였습니다.
최대 읽기 PC 관측 횟수는 워밍업/경계를 포함해 511회입니다.
원본 로그는 `Tests\Modbus\VagabondK.nF.Modbus.SerialClientFixture\bin\ascii-client.log`이며,
시험 후 기존 RTU Server를 다시 배포하고 RTU 확장 시험도 통과했습니다.

## 실물 결과

2026-10-23, Geekble Nano ESP32-S3 / CLR 1.18.0.30에서 COM3를 닫고
USB 출력 수신기 없이 최종 시험을 통과했습니다. PC READY부터 완료까지
260.4초였으며 라이브러리 코드는 변경하지 않고 ResponseLength 모드를 사용했습니다.
처음 배포에서 누락된 전이 참조 `nanoFramework.System.Text`를 앱 프로젝트에
추가한 뒤 실행했습니다. 네이티브 펌웨어는 변경하지 않았습니다.

| 항목 | 결과 |
|---|---|
| 8개 함수와 최대 수량 | 읽기 2000 bit / 125 Register, 쓰기 1968 Coil / 123 Register 및 되읽기 성공 |
| 응답 길이 경계 | 119/120/121/123/239/240/241/243/255바이트 데이터/CRC 검증 성공 |
| 반복 읽기 | 최대 Register 읽기 500회 비교 성공. 경계/워밍업 포함 PC 관측 총 511회 |
| 브로드캐스트 | 쓰기 4종 후 되읽기 성공 |
| 오류/지연 | 예외 02, 무응답, CRC 오류, 부분 응답, 500ms 지연, 2500ms 늦은 응답을 각각 판정하고 정상 읽기 복구 |
| GC 후 가용 힙 | 기준 759,264바이트, 100/200/300/400/500회 시점 모두 동일 |

오류 요청부터 MCU 복구 마커까지 PC에서 관측한 시간은 다음과 같습니다.
**3초 의도적 대기, 버퍼 비우기, 정상 읽기와 마커 전송을 포함**하므로
순수 응답 시간이나 Client 제한 시간의 정확도 측정으로 해석하면 안 됩니다.
CLR 예외 출력에 따른 추가 지연 가능성도 제거하지 않았습니다.

| 주입 | 복구 마커까지 |
|---|---|
| Modbus 예외 02 | 5000ms |
| 무응답 | 6656ms |
| CRC 오류 | 5234ms |
| 부분 응답 | 6704ms |
| 정상 500ms 지연 | 3750ms |
| 2500ms 늦은 응답 | 6656ms |

원본 로그는 `Tests\Modbus\VagabondK.nF.Modbus.SerialClientFixture\bin\rtu-client-final.log`입니다.
시험 후 PC 포트를 닫고 기존 RTU Server 앱으로 복원했습니다.
이 관측 구간에서 가용 힙이 같았다는 사실만으로 장기 누수 부재를 단정하지 않습니다.

## 일반 Server 복원

시험 종료 후 기존 RTU Server로 돌아가려면 프로토콜을 명시해 다시 빌드하고
배포합니다. RTU/ASCII가 같은 출력 경로를 쓰므로 이미지 이름만으로 구별하면 안 됩니다.

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' .\Samples\Modbus\VagabondK.nF.Modbus.SerialServer\VagabondK.nF.Modbus.SerialServer.nfproj /t:Rebuild /p:Configuration=Release /p:SerialProtocol=Rtu /v:minimal /nologo
nanoff --nanodevice --deploy --serialport COM3 --image .\Samples\Modbus\VagabondK.nF.Modbus.SerialServer\bin\Release\VagabondK.nF.Modbus.SerialServer.bin --suppressnanoffversioncheck
```

Server의 메모리 데이터는 앱 재시작 시 초기값으로 돌아갑니다.
