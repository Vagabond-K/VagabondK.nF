# ESP32 TCP Client 시험기

PC 시험 서버 전용 샘플입니다. 실제 PLC나 설비를 대상으로 실행하지 마십시오.
이 샘플은 Coil 0..1967, Holding Register 0..124, 127, 200..205에 값을 씁니다.
PC 시험 서버는 매번 새 메모리 저장소를 생성하므로 실제 I/O에는 영향이 없습니다.

`Program.cs`의 `ServerAddress`를 PC의 IPv4 주소로 설정합니다.
기본 시험 환경은 PC `192.168.0.210:15020`, ESP32 `192.168.0.187`입니다.
Wi-Fi는 TCP Server 샘플과 마찬가지로 장치에 저장한 DHCP/AutoConnect 설정을 사용합니다.
Client 역할에는 Server DLL이 필요하지 않습니다.

## 실행

루트 `packages.config`를 복원한 뒤 아래를 실행합니다.

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' .\Samples\Modbus\VagabondK.nF.Modbus.TCPClient\VagabondK.nF.Modbus.TCPClient.nfproj /p:Configuration=Release /v:minimal
dotnet run --project .\Tests\Modbus\VagabondK.nF.Modbus.ClientFixture\VagabondK.nF.Modbus.ClientFixture.csproj -c Release -- 192.168.0.210 192.168.0.187
```

PC 서버가 READY를 출력하면 다른 터미널에서:

```powershell
nanoff --nanodevice --deploy --serialport COM3 --image .\Samples\Modbus\VagabondK.nF.Modbus.TCPClient\bin\Release\VagabondK.nF.Modbus.TCPClient.bin
```

Windows 방화벽이 앱 수신 허용을 요청할 수 있습니다. 신뢰할 수 있는 시험망에서만
허용하십시오. 방화벽 전체를 끄지 마십시오. 시험 서버는 지정된 PC IP에만 바인딩하고,
지정된 보드 IP 이외의 연결을 거부하며 15분 후 실패 종료합니다.
첫 연결이 방화벽 설정 전에 실패했다면 PC 서버와 보드 앱을 다시 시작합니다.
보드 앱은 자동 접속/쓰기 재시도를 하지 않으며 실패는 처리되지 않은 예외로 명확히 드러냅니다.

## 시험 내용

- 8개 함수, Coil 읽기 2000개/쓰기 1968개, Register 읽기 125개/쓰기 123개.
- 부호 없는 16비트 값과 경계 주소의 값 검증.
- 최대 Register 읽기 10000회 및 각 응답의 데이터 검증.
- 2000회마다 강제 GC 후 가용 힙 기록. 로그 문자열은 PC에서만 만듭니다.
- 잘못된 주소의 Modbus 예외, 응답 생략에 대한 타임아웃,
  잘못된 Transaction ID, 상대 연결 종료 감지.
- 오류 후 연결 폐기 및 새 연결에서 정상 읽기 재개.

PC는 각 응답을 두 번의 Write로 나눕니다. 실제 패킷 분할 여부는 TCP 스택에 따라
달라지므로 이 시험만으로 모든 분할 수신 패턴을 보장하지 않습니다.
별도 PC 회귀 검증은 1바이트 단위 수신을 강제로 시험합니다.

주소 60000..60003은 PC 시험기의 오류 주입 지점이며 일반 Modbus 기능이 아닙니다.
Holding Register 127에 쓰는 완료 마커 `0x600D`는 MCU 검증이 전부 끝난 뒤에만 보냅니다.
PC는 완료 마커뿐 아니라 함수 종류, 반복 읽기 횟수, 오류 주입 4종,
5개 연결 및 메모리 보고 5회를 확인한 뒤에만 PASS로 종료합니다.

MCU의 GC 후 가용 힙은 네트워크와 런타임의 영향도 받는 관찰값입니다.
반복 중 감소 추세를 확인하는 데 사용하며, 짧은 시험만으로 메모리 누수가 없다고
단정하지 않습니다. 강제 GC 측정은 시험용이며 실시간 운영 경로에 권장하지 않습니다.

완료 후 PC 시험 서버는 종료하고 MCU는 무한 대기합니다.
TCP Server 샘플로 돌아가려면 해당 샘플의 BIN을 다시 배포합니다.

## 확인한 실기 결과

Geekble Nano, `ESP32_S3_QUAD` 1.18.0.30, Release 빌드에서
8개 함수와 최대 수량, 10,000회 반복 읽기, 오류 주입 4종,
오류 후 새 연결을 포함한 5개 연결을 확인하고 PC 시험기가 PASS로 종료했습니다.
최대 읽기는 초기 확인과 웜업을 포함해 총 10,011회였습니다.
2000/4000/6000/8000/10000회 시점의 강제 GC 후 가용 힙은
모두 **759,804바이트**로, 기준 대비 변화는 0이었습니다.
이 값은 해당 실행에서의 관찰 결과이며 다른 펌웨어·빌드의 보장값이 아닙니다.
