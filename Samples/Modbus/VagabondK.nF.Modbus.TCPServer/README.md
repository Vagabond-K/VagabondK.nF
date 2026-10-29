# ESP32-S3 Modbus TCP Server

Geekble Nano에서 첫 TCP 통신을 확인하기 위한 nanoFramework 실행 프로젝트입니다.
RX/TX 배선이나 RS-485 모듈 없이 USB 배포와 2.4GHz Wi-Fi만 사용합니다.
자격 증명은 소스 코드에 넣지 않고 장치 네트워크 설정에 저장합니다.

## 장치 준비

확인된 Geekble Nano의 구성: ESP32-S3 revision 0.2, Flash 4MB, PSRAM 2MB.
이 장치에서 `ESP32_S3_QUAD` 펌웨어 1.18.0.30의 USB nanoFramework 응답을 확인했습니다.
다른 리비전에는 같은 설정이 맞는다고 가정하지 마십시오.
펌웨어 설치 후 자동 리셋에서 USB 핸들 오류가 나면 BOOT을 누르지 않고
USB 전원을 재연결한 뒤 `nanoff --listdevices`로 확인합니다.
현재 장치 포트는 COM3이며 재연결 시 달라질 수 있습니다.

## 빌드와 배포

솔루션 루트에서 실행합니다. MSBuild 설치 경로는 환경에 맞게 지정합니다.

```powershell
nuget restore .\packages.config -PackagesDirectory .\packages -NonInteractive
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' .\Samples\Modbus\VagabondK.nF.Modbus.TCPServer\VagabondK.nF.Modbus.TCPServer.nfproj /p:Configuration=Release /v:minimal
nanoff --nanodevice --deploy --serialport COM3 --image .\Samples\Modbus\VagabondK.nF.Modbus.TCPServer\bin\Release\VagabondK.nF.Modbus.TCPServer.bin
```

앱 배포는 기존 관리 애플리케이션을 교체합니다. 펌웨어를 다시 설치하는 명령이 아닙니다.

## Wi-Fi 설정

1. `VagabondK.nF.sln`을 Visual Studio에서 엽니다.
2. nanoFramework Device Explorer에서 연결된 COM 포트의 장치를 선택합니다.
3. 네트워크 설정 편집에서 2.4GHz Wi-Fi의 SSID/비밀번호를 입력하고,
   Wi-Fi 활성화/자동 연결 및 DHCP/자동 DNS를 설정한 뒤 저장합니다.
4. 보드를 재시작합니다.
5. 이 샘플을 시작 프로젝트로 지정하고 F5로 실행하면 디버그 출력에 IP 주소가 표시됩니다.

Wi-Fi 비밀번호는 채팅이나 소스 파일에 입력하지 마십시오.
PC와 보드는 서로 통신 가능한 네트워크에 있어야 합니다. 게스트 Wi-Fi의
클라이언트 격리, 방화벽, 서로 다른 서브넷을 확인하십시오.
프로그램은 IP를 받을 때까지 10초 간격으로 상태를 출력하며 대기합니다.
별도 Wi-Fi 접속 도우미 패키지 없이 펌웨어의 저장된 AutoConnect 설정을 사용합니다.

## PC에서 시험

PC Modbus TCP Client의 연결 설정:

| 항목 | 값 |
|---|---|
| 주소 | 디버그 출력의 보드 IP |
| 포트 | 502 |
| Unit ID | 1 |
| 시작 주소 | 0 (도구에 따라 1 또는 40001로 표기) |
| Holding Register 0..2 | 123, 456, 65535 |
| Input Register 0..2 | 100, 200, 300 |
| Discrete Input 0..3 | true, false, true, false |
| Coil 초기값 | false |

함수 03으로 Holding Register 3개를 읽고, 06/10으로 쓴 뒤 다시 읽습니다.
Coil은 01/05/0F, Discrete Input은 02, Input Register는 04로 확인합니다.
주소 128의 Register 읽기는 IllegalDataAddress 응답이어야 합니다.
PC에서 별도 Modbus 도구 없이 아래 스크립트로도 시험할 수 있습니다.
실행 중인 이 샘플을 대상으로만 사용하십시오.

```powershell
.\Samples\Modbus\VagabondK.nF.Modbus.TCPServer\Test-TcpServer.ps1 -Address 192.168.0.187
```

8가지 함수, 분할 요청, 예외 응답, 반복 읽기를 검사합니다.
Coil 및 Holding Register 주소 10..11을 시험 중 변경한 뒤 원래 값으로 복원합니다.
다른 클라이언트와 동시에 실행하지 마십시오. 통신이 끊기면 복원이 실패할 수 있습니다.

최대 Register 수량, 잘못된 요청, 연속 ADU, 재접속을 추가로 검사하려면:

```powershell
.\Samples\Modbus\VagabondK.nF.Modbus.TCPServer\Test-TcpServer.ps1 -Address 192.168.0.187 -Extended -Iterations 10000 -ReconnectCount 20
```

확장 시험은 Holding Register 0..122를 패턴으로 덮어쓴 뒤 복원합니다.
시험 중 다른 클라이언트나 실제 설비가 데이터를 사용하면 안 됩니다.
읽기 125개, 쓰기 123개, 주소 범위 초과 시 부분 쓰기 방지, 잘못된 함수/값,
한 번의 송신에 포함된 두 ADU, 비정상 MBAP로 연결 종료 후 재접속을 확인합니다.
최신 샘플은 1000개 요청마다 강제 GC 후 가용 힙을 측정하고,
확장 스크립트는 2000회 읽기마다 그 결과를 출력합니다.
초기 경로의 웜업과 연결별 버퍼 수명을 고려해 값을 해석해야 합니다.
이는 시험용 진단이며 실시간 운영 코드에 강제 GC를 넣으라는 의미가 아닙니다.
수백/수천 회의 반복 성공은 수시간 이상의 안정성이나 메모리 누수 부재를 보장하지 않습니다.
최대 Coil 수량은 현재 64개 저장소에서 시험하지 않으며 별도 구성이 필요합니다.
디버거를 연결해도 되지만 중단점에서 멈추지 말고 프로그램이 계속 실행되게 하십시오.

`-Extended -TimeoutRecovery`를 지정하면 서버가 유휴 연결과 부분 MBAP 연결을
각각 30초 후 폐기하는지 확인하고 새 연결의 정상 동작도 검사합니다.
추가로 약 1분이 필요합니다. 타임아웃 시험 중에는 다른 요청을 보내지 마십시오.

Input Register 120..127은 최신 샘플의 시험 진단 영역입니다.

| 주소 | 의미 |
|---|---|
| 120 | 진단 마커 0x4E45 (첫 1000개 요청 전에는 0) |
| 121..122 | 처리한 요청 수, UInt32 상위 워드 먼저 |
| 123..124 | 첫 측정의 GC 후 가용 바이트 수 |
| 125..126 | 최근 측정의 GC 후 가용 바이트 수 |
| 127 | 측정 횟수, UInt16 |

데이터 저장소는 64 Coils, 64 Discrete Inputs, 128 Holding Registers,
128 Input Registers입니다. 전원 재시작 시 초기값으로 돌아갑니다.

연결은 한 번에 하나씩 처리하며, 수신 제한 시간은 30초입니다.
타임아웃/프레임/소켓 오류를 기록하고 해당 연결을 닫은 뒤 다시 Accept합니다.
부분 프레임 수신 상태를 재사용하지 않기 위해 타임아웃 후에도 연결을 폐기합니다.
예상하지 못한 애플리케이션 예외는 숨기지 않습니다.

`System.Net`은 `System.Threading`, `System.IO.Streams`,
`nanoFramework.System.Text`도 필요로 합니다. `Build\Network.props`에서
TCP 라이브러리와 샘플이 같은 의존성 집합을 참조하도록 관리합니다.
`Link failure`가 발생하면 루트 패키지 복원 후 샘플을 다시 빌드·배포하십시오.

인증/암호화가 없는 시험용 서버입니다. 신뢰할 수 있는 테스트망에서만 사용하고
실제 설비의 출력이나 제어 명령에는 연결하지 마십시오.

## 확인한 실기 결과

Geekble Nano, `ESP32_S3_QUAD` 1.18.0.30, Release 빌드에서 다음 명령으로
**10,159개 검증을 통과**했습니다.

```powershell
.\Samples\Modbus\VagabondK.nF.Modbus.TCPServer\Test-TcpServer.ps1 -Address 192.168.0.187 -Extended -Iterations 10000 -ReconnectCount 20 -TimeoutRecovery
```

최대 Register 읽기 10,000회에 약 134.3초가 걸렸으며,
2000/4000/6000/8000/10000회 측정 시 GC 후 가용 힙은 모두
**759,348바이트**, 기준 대비 변화 0이었습니다.
20회 재접속, 비정상 MBAP 이후 복구, 유휴 연결 및 부분 MBAP의
30초 타임아웃 후 새 연결에서 정상 읽기를 확인했습니다.
쓰기 시험에서 바꾼 값도 복원했습니다.
실행 속도와 메모리는 해당 시험 환경의 관찰값이며 성능 보장값이 아닙니다.
