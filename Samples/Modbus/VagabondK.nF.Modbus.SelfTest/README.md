# MCU 프로토콜 자체 검사

추가 배선 없이 **실제 nanoFramework CLR 위에서** RTU/ASCII 코덱과
Client/Server 통합 동작을 확인하는 시험 전용 앱입니다.
UART, RS-485 방향 제어, 실제 프레임 시간 간격을 시험하는 앱이 아닙니다.
메모리 채널은 동기 콜백과 고정 버퍼로 두 역할을 연결합니다.

알려진 CRC/LRC 벡터와 손상 프레임 거부, 8개 함수,
최대 Coil/Register 수량, 브로드캐스트, 예외 응답,
각 전송 방식의 최대 Register 읽기 1000회를 확인합니다.
예열 후 강제 GC로 얻은 반복 전후의 가용 힙도 수집합니다.
실패하면 예외로 중단하며 성공 보고 서버는 열리지 않습니다.

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' .\Samples\Modbus\VagabondK.nF.Modbus.SelfTest\VagabondK.nF.Modbus.SelfTest.nfproj /t:Rebuild /p:Configuration=Release /v:minimal
nanoff --nanodevice --deploy --serialport COM3 --image .\Samples\Modbus\VagabondK.nF.Modbus.SelfTest\bin\Release\VagabondK.nF.Modbus.SelfTest.bin
```

시험에는 MCU 실행 속도에 따라 수 분이 걸릴 수 있습니다.
모든 시험이 성공한 뒤에만 저장된 Wi-Fi 설정으로 TCP 502 서버를 엽니다.
완료 전 결과 조회의 연결 거부는 실패 판정이 아닙니다. 충분히 기다려도
서버가 열리지 않으면 Visual Studio 디버거에서 처리되지 않은 예외를 확인하십시오.
손상 CRC/LRC 및 잘못된 주소 시험에서 발생하는 처리된 예외 출력은 정상입니다.
Unit ID 1, 함수 04, 주소 0부터 12개의 Input Register를 읽습니다.
한 연결에서 요청 하나만 처리합니다.

```powershell
.\Samples\Modbus\VagabondK.nF.Modbus.SelfTest\Get-SelfTestResult.ps1 -Address 192.168.0.187
```

| 주소 | 값 |
|---|---|
| 0 | 성공 마커 0x600D |
| 1..2 | 검증 횟수 UInt32, 상위 워드 먼저 |
| 3..4 | RTU 반복 전 GC 후 가용 바이트 수 |
| 5..6 | RTU 반복 후 GC 후 가용 바이트 수 |
| 7..8 | ASCII 반복 전 GC 후 가용 바이트 수 |
| 9..10 | ASCII 반복 후 GC 후 가용 바이트 수 |
| 11 | 방식별 반복 횟수 1000 |

이 앱은 시험 목적상 Client와 Server DLL을 모두 참조합니다.
실제 제품의 한 역할에 두 DLL이 모두 필요한 것은 아닙니다.
정밀 타이밍·UART/RS485 시험은 외부 장비를 준비한 뒤 별도로 진행해야 합니다.

## 확인한 실기 결과

Geekble Nano, `ESP32_S3_QUAD` 1.18.0.30, Release 빌드에서
성공 마커와 **10,291개 검증 통과**, 방식별 1000회 반복 완료를 TCP로 확인했습니다.

| 항목 | 반복 전 GC 후 가용 바이트 수 | 반복 후 GC 후 가용 바이트 수 | 변화 |
|---|---:|---:|---:|
| RTU 메모리 채널 | 749,988 | 749,556 | -432 |
| ASCII 메모리 채널 | 749,004 | 749,004 | 0 |

RTU에서 관찰한 432바이트 감소의 원인은 이 두 시점 측정만으로 판정할 수 없습니다.
누수 부재나 장기 안정성의 근거로 사용하지 마십시오.
메모리 채널은 수신 대기 시간을 실제 UART처럼 재현하지 않으며,
보레이트·프레임 간격·RS-485 방향 전환을 검증하지 않습니다.
