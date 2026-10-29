# 독립 DNP3 TCP 실물 시험 호스트

저장된 Wi-Fi AutoConnect 설정을 사용하는 MCU와 원본 OpenDNP3 3.1.2를 직접 연결합니다.
PC 내부 루프백이나 UART 데이터를 TCP로 전달하는 가상 채널 또는 게이트웨이 시험이 아닙니다.
기본 빌드는 포트/소켓을 열지 않습니다. `Dnp3HardwareTest=true`가 필요한 격리 시험 전용 앱입니다.
물리 출력 제어는 없으며 기존 시리얼 시험과 같은 7종 x 8포인트 저장소를 사용합니다.
검증 데이터/시퀀스를 공유하기 위해 이 시험 앱에는 Serial DLL도 참조되지만 UART는 열지 않습니다.
일반 TCP 애플리케이션에는 코어 + 역할 + TCP DLL만 필요합니다.

## 확인한 결과

| 경로 | 상태 | 증거 (`bin\dnp3-hardware`) |
|---|---|---|
| OpenDNP3 PC Master → MCU Outstation | 실제 Wi-Fi `192.168.0.187:20000`, 45종·56포인트·4종 제어/재조회·int32 경계·비요청·100회 읽기 + 시간/품질/Counter 경계·동일 시각 이벤트 순서; 7,546개 검사 통과 | `independent-tcp-master.log` |
| MCU Master → OpenDNP3 PC Outstation | 실제 Wi-Fi `192.168.0.187` → `192.168.0.210:20000`, 동일 확대 시퀀스; MCU 7,420개 검사, CRC 오류 0, 제어 45회, UTC `1700000001324`, 완료 값 재조회 확인 | `independent-tcp-outstation-mcu.log`, `independent-tcp-outstation.log` |

두 번째 경로는 최초에 Windows 방화벽의 명시적 차단으로 연결 단계에서 실패했습니다.
사용자가 Windows 허용 창에서 허용한 뒤 TCP 인바운드 허용 규칙과 개인 네트워크 적용을
확인하고 2026-10-24 재시험을 통과했습니다. 방화벽을 임의로 변경하거나 우회하지 않았습니다.
PC READY/완료 마커뿐 아니라 MCU의 `DNP3 MCU TCP MASTER PASS`를 확인하여
45종 표현, 제어·재조회, int32 경계값, 비요청과 100회 읽기의 전체 완료를 판정했습니다.
이전 차단 당시 로그는 같은 파일명의 `.before-firewall-allow` 사본으로 보존했습니다.
다른 PC에서 재실행할 때도 다음 실행 파일과 시험 주소의 TCP 인바운드 허용이 필요합니다.

`bin\protocol-tools\master-build\Release\VagabondKDnp3Outstation.exe`

시리얼 독립 시험은 양방향 모두 통과했으며 [시리얼 문서](../VagabondK.nF.DNP3.SerialHost/README.md)에 있습니다.
같은 문서의 확대 정합성 절에 PC 코덱 92,160개 품질/시간 조합과 수치 경계 516개,
실물 48비트 시각 최소/최대·역행·동일 포인트/동일 시각 이벤트 순서 검사의 범위를 기록했습니다.
확대 실물 시험은 대표 품질 패턴과 전체 비트 폭을 사용하는 Static 표현 및 시각이 있는 Event 표현을 사용합니다.
45종 전부에 256개 품질/8개 시간의 전 조합을 적용한 것은 PC 코덱 시험이며,
실물 전체 조합이나 실제 RTC 정확도/드리프트를 검증했다는 뜻은 아닙니다.
검사 횟수는 폴링 중 검사도 포함하므로 실행 시간에 따라 달라지며 데이터 건수/처리량이 아닙니다.
OpenDNP3 원본의 subnormal/-0 한계는 별도 수치 회귀 문서의 제한을 그대로 유지합니다.
이 실물 시험은 유한 정수/일반 부동소수점 값과 int32 경계값을 다루며
원본의 모든 IEEE754 경계값 문제가 해결되었다는 의미는 아닙니다.
별도의 [Python/Go PC 루프백 TCP 시험](../../../Tests/DNP3/VagabondK.nF.DNP3.ExtraPeers/README.md)에서는
subnormal/음의 0 보존을 확인했지만, 이 새 피어들과의 MCU Wi-Fi 시험은 아닙니다.

## 빌드와 실행

MCU 주소는 저장된 Wi-Fi 설정에서 얻습니다. 소스에 SSID/비밀번호를 넣지 않습니다.
Master 시험의 PC 주소는 격리 평가용 `192.168.0.210:20000`이며 다른 네트워크에서는
`Program.cs`의 시험 주소와 PC 시험기의 인자를 함께 맞추십시오.

```powershell
.\Build\Build-Dnp3ValidationMaster.ps1
dotnet build .\Tests\DNP3\VagabondK.nF.DNP3.SerialFixture\VagabondK.nF.DNP3.SerialFixture.csproj -c Release
MSBuild .\Samples\DNP3\VagabondK.nF.DNP3.TCPHost\VagabondK.nF.DNP3.TCPHost.nfproj /t:Rebuild /p:Configuration=Release /p:Dnp3HardwareTest=true /p:Dnp3Role=Outstation
nanoff --nanodevice --deploy --serialport COM3 --image .\Samples\DNP3\VagabondK.nF.DNP3.TCPHost\bin\Release\VagabondK.nF.DNP3.TCPHost.bin
# MCU의 IP/READY 확인 후:
dotnet .\Tests\DNP3\VagabondK.nF.DNP3.SerialFixture\bin\Release\net10.0\VagabondK.nF.DNP3.SerialFixture.dll 192.168.0.187 IndependentMaster
```

반대 방향은 `Dnp3Role=Master`로 Rebuild하고 생성 이미지를
`bin\dnp3-hardware\Dnp3.TcpMaster.bin`에 보관한 뒤 아래를 실행합니다.

```powershell
.\Build\Test-Dnp3IndependentPeer.ps1 -Transport Tcp
```

MCU Outstation 이미지를 `bin\dnp3-hardware\Dnp3.TcpOutstation-boundaries.bin`에 보관한 뒤
`.\Build\Test-Dnp3IndependentPeer.ps1 -Transport Tcp -PeerRole Master`로 반대 역할도 실행할 수 있습니다.
이때 MCU CLR을 재시작하고 실제 Wi-Fi TCP 수신 준비를 확인한 다음 PC Master를 시작합니다.
준비 확인용 연결은 종료하므로 MCU가 연결 종료를 기록하고 다음 시험 연결을 받는 것은 정상입니다.

기본 역할에서는 PC OpenDNP3 Outstation을 시작하고 MCU를 재시작하여
`DNP3 MCU TCP MASTER PASS`와 PC의 제어 45회·UTC·완료 마커를 모두 요구합니다.
MCU 예외, 피어 실패, 기한 초과는 명시적인 시험 실패로 처리합니다.
시험 종료 후 원래 앱을 복원하는 것은 별도 절차입니다.
본 작업에서는 SHA256을 확인한 Modbus 원본 이미지를 복원하고 기존 RS485에서
holding `123,456,65535` 읽기를 재확인했습니다.
최신 확대 시험 후 복원 증거는 `bin\dnp3-hardware\restored-modbus-boundaries.log`입니다.
다른 현장 장비·장기 MCU 힙 사용량·처리량 한계·TLS/인증·전원 차단 내성은 검증 범위에 포함되지 않습니다.
