# Geekble Nano 시리얼 서버

DM2472 자동 방향 제어 RS-485 모듈용 시험 샘플입니다.
실제 설비가 아닌 시험 장치에만 사용하십시오.
배포하면 보드의 기존 관리 앱을 교체합니다.

현재 RTU 앱은 **길이 기반 + 상태 반환 수신**을 사용합니다.
ASCII 앱도 **상태 반환 수신**을 사용합니다.
COM3를 닫고 디버거 없이 확장 시험을 통과했습니다. 아래 초기 실패 기록은
원인 분석 이력이며, 최신 결과는 「상태 기반 수신 적용」 절을 참조하십시오.

| 항목 | 설정 |
|---|---|
| MCU UART | COM2 (PC의 COM2와 무관) |
| TX | D8 / GPIO17 |
| RX | D9 / GPIO18 |
| 기본 통신 | RTU, 9600bps, 8E1, Unit ID 1 |
| RTU 수신 경계 | `RtuReceiveMode.RequestLength` (함수·바이트 수 기반) |
| ASCII 선택 시 | 9600 7E1, `:` / CRLF, 문자 사이 최대 1초, 전체 제한 3초 |
| Holding 0..2 초기값 | 123, 456, 65535 |
| 저장소 | Coil/Discrete 각 2000개, Holding/Input Register 각 128개 |

Nano 5V는 DM2472 VCC 및 레벨 변환기 HV로, Nano 3V3은 LV로 연결합니다.
GND는 공통입니다. GPIO17은 LV3-HV3을 거쳐 DM2472 RXD로,
DM2472 TXD는 HV2-LV2를 거쳐 GPIO18로 연결합니다.
DM2472 단자 방향은 보드 뒷면 배선도 기준입니다.
5V 신호를 Nano GPIO에 직결하지 마십시오.
모듈이 방향을 제어하므로 UART RS485/RTS 모드는 사용하지 않습니다.

```powershell
nuget restore .\packages.config -PackagesDirectory .\packages -NonInteractive
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' .\Samples\Modbus\VagabondK.nF.Modbus.SerialServer\VagabondK.nF.Modbus.SerialServer.nfproj /t:Rebuild /p:Configuration=Release /v:minimal
nanoff --nanodevice --deploy --serialport COM3 --image .\Samples\Modbus\VagabondK.nF.Modbus.SerialServer\bin\Release\VagabondK.nF.Modbus.SerialServer.bin
```

Visual Studio에서 이 프로젝트를 시작 프로젝트로 지정할 수도 있습니다.
PC 어댑터 포트는 별도로 선택합니다. 확인한 PC 환경은 FTDI COM6이며,
그 뒤에 RS-232 to RS-485 컨버터가 연결되어 있습니다.
RTS/DTR의 전원 공급 또는 방향 제어 용도는 컨버터 사양을 확인해야 합니다.

아래 읽기 전용 시험은 RTS/DTR을 모두 활성화합니다.
System.IO.Ports가 기본 제공되는 Windows PowerShell 5.1에서 실행하거나,
PowerShell 7에서는 해당 DLL을 먼저 로드하십시오.
종료 시 포트를 닫으므로 RTS/DTR 상태가 계속 유지된다고 가정하지 마십시오.

```powershell
.\Samples\Modbus\VagabondK.nF.Modbus.SerialServer\Test-SerialServer.ps1 -PortName COM6
```

초기 실물 시험에서는 통신을 확인하지 못했습니다.
의존성 누락을 수정한 후 MCU UART 수신 루프 실행은 확인했지만,
COM6에서 RTS/DTR을 모두 활성화한 요청에도 수신 바이트는 0이었습니다.
원인이 컨버터 방향 제어인지, 전원/배선/극성/신호 레벨인지 아직 확정하지 않았습니다.

후속 진단에서 ESP32 UART 드라이버가 요청한 바이트 수 전체를 기다리는
동작을 확인하여 `SerialPortChannel`을 수신 버퍼에 있는 수량만 읽도록 수정했습니다.
현재 배선에서 MCU가 요청 `01 03 00 00 00 0A C5 CD`를 수신하고
정상 응답 25바이트를 UART Write에 전달하는 것까지 확인했습니다.
이 단계에서는 PC에서 정상 응답을 받지 못했고, 간헐적인 `3F`와 `RXParity`가
관찰되었습니다. UART Write 완료만으로 RS-485 버스 및 PC 수신 성공을 판단하지 않습니다.

송신 경로를 레벨 변환기의 3번 채널로 옮겼습니다.
현재 송신 배선은 GPIO17 → LV3/HV3 → DM2472 RXD이며,
수신은 DM2472 TXD → HV2/LV2 → GPIO18입니다.
변경 직후 정상 응답 4회 후 다시 무응답이 발생했고,
진단 출력을 끄는 것만으로는 회복되지 않았습니다.
양쪽 USB를 모두 분리해 10초 기다린 뒤 Nano, PC 어댑터 순으로
재연결하자 최대 10개 Register 읽기 요청 10회와
PC 포트 닫기/다시 열기 3회에서 모두 정확한 25바이트 응답을 확인했습니다.
CRC 및 Holding 0..2의 123,456,65535 값도 일치했습니다.
이것은 9600 8E1 기본 읽기의 초기 실물 성공이며,
이전 간헐 장애의 원인이나 장기 안정성을 확정한 결과는 아닙니다.
1번 변환 채널 불량 또는 컨버터 전원 부족이라고 단정하지 않습니다.

## 확장 실물 시험과 현재 제한

동일한 9600 8E1 구성, `ESP32_S3_QUAD` 1.18.0.30에서 후속 시험했습니다.
아래 요청 길이는 주소와 CRC를 포함한 RTU ADU 길이입니다.
다음 표는 **길이 기반 모드 적용 전**의 무통신 간격 기반 수신 결과입니다.

| 항목 | 결과 |
|---|---|
| 01/02 읽기 | Coil/Discrete 각 2000개 읽기 응답 확인 |
| 03/04 읽기 | Holding/Input Register 각 125개 읽기 응답 확인 |
| 05/06 쓰기 | 단일 쓰기 응답 및 읽어 되돌려 확인 |
| 0F 쓰기 | 800/880/888 Coil 쓰기 및 되읽기 성공: 요청 109/119/120바이트 |
| 10 쓰기 | 50/55 Register 쓰기 및 되읽기 성공: 요청 109/119바이트 |
| 121바이트 요청 | 896 Coil 및 56 Register 쓰기 모두 무응답 |
| 255바이트 요청 | 최대 1968 Coil 쓰기 무응답; 최대 123 Register 쓰기는 전체 시험에서 아직 도달하지 못함 |
| 브로드캐스트 | 05/06/0F/10 무응답 및 데이터 반영 확인 |
| 예외 응답 | 주소/수량/미지원 함수에 대한 예외 코드 및 CRC 확인 |
| 손상 CRC 후 복구 | USB 디버거 연결 시 무응답 후 다음 정상 요청 및 125 Register 읽기 25회 성공 |
| 복원 | 소분 쓰기 후 전체 백업 영역 일치, 별도 `-Restore` 경로도 실물 확인 |

최대 Coil 쓰기 진단에서 MCU는 255바이트 요청을 한 프레임으로 받지 못하고
120바이트 단위로 읽은 뒤 CRC 오류로 폐기했습니다. 진단 출력을 끈 빌드에서도
120/121바이트 경계의 성공/실패가 재현되었습니다. 현재 `BytesToRead`는
네이티브 드라이버가 관리 버퍼에 전달한 데이터만 보여 주므로,
관리 버퍼가 잠시 비었다고 실제 RS-485 버스가 무통신이라고 판단할 수 없습니다.
이는 UART 버퍼링과 관리 코드 프레임 종료 판정의 충돌을 가리킵니다.
정확한 전선상의 시간 간격은 파형 장비로 측정하지 않았습니다.

USB 디버거를 연결하지 않은 상태에서는 손상 CRC 후 첫 정상 요청이
타임아웃되었고, 연결 상태에서는 동일한 대기 조건으로 성공했습니다.
재배포 후 기본 읽기가 무응답이었다가 RTS/DTR을 비활성화한 뒤 재활성화하여
회복한 경우도 있습니다. 이 두 현상과 앞서 관찰한 간헐 장애는 원인이
확정되지 않았으며, 디버거 없이 독립 실행하는 안정성을 보장하지 않습니다.
확장 시험 전체 통과나 산업용 적합성을 주장하지 않습니다.

프레임 대기 시간을 임의로 늘리거나 쓰기를 자동 재전송해 문제를 숨기지 않습니다.
정확한 RTU 무통신 경계 감지가 필요하면 네이티브 UART 경계 정보가 필요합니다.
`ReceivedBytesThreshold`는 관리 이벤트 임계값이므로 하드웨어 FIFO 임계값과
같다고 가정해서는 안 됩니다.

### 길이 기반 수신 적용 결과

펌웨어는 변경하지 않고 샘플의 RTU 수신을 `RtuReceiveMode.RequestLength`로
변경했습니다. 01..06 요청은 8바이트, 0F/10 요청은 바이트 수 필드가 가리키는
길이까지 전체 제한 시간 안에서 기다립니다. 관리 버퍼가 5ms 비어 있다는 이유로
부분 프레임을 끝내지 않습니다. CRC와 최대 ADU 길이는 계속 검사합니다.
사용자 요청에 따라 종단저항 없는 배선을 그대로 유지했습니다.

디버거를 연결하지 않은 초기 실행에서도 최대 1968 Coil 및 123 Register
쓰기와 되읽기를 통과했습니다. 이후 CRC 오류 후 복구 구간에서 무응답이
발생했고 즉시 복원도 실패했지만, 다음 `-Restore` 실행에서 백업과 일치하도록
복원했습니다. 재배포나 백업 삭제로 실패를 숨기지 않았습니다.

USB 디버거를 연결한 실행에서는 다음을 한 번의 `-Extended -Iterations 100`으로
모두 통과하고 백업 데이터를 복원했습니다.

| 항목 | 결과 |
|---|---|
| Coil 쓰기 요청 길이 | 119/120/121/239/240/241/255바이트, 응답 및 되읽기 일치 |
| 최대 Register 쓰기 | 123개, 255바이트 요청 성공 |
| 최대 크기의 잘못된 요청 | 256바이트 수신 후 잘못된 바이트 수 필드에 대한 예외 03 응답 |
| 기본 기능 | 8개 함수, 최대 수량 읽기, 브로드캐스트 4종, 주소/수량/미지원 함수 예외 |
| 반복 | 125 Register 읽기 100회, 45.4초, 모든 데이터/CRC 일치 |
| 손상 CRC | 무응답 확인 후 정상 읽기 복구 |
| 종료 상태 | 전체 백업 영역 복원 및 비교 성공 |

따라서 길이 기반 수신은 긴 프레임의 관리 버퍼 배치 분할 문제를 해결했습니다.
이 단계에서는 **디버거 없는 독립 실행의 간헐 무응답과 오류 후 복구 문제가 남았습니다**.
디버거 연결 조건의 전체 통과를 무조건적인 통신 안정성으로 해석하지 마십시오.
새 모드도 정확한 t1.5/t3.5 검증은 아니며, 바이트가 전체 제한 시간 안에
도착하면 프레임 내부의 긴 전달 간격을 허용합니다.

`RequestLength`는 서버, `ResponseLength`는 클라이언트에 사용합니다.
지원하는 8개 함수와 예외 응답에는 길이 규칙을 적용하고, 알 수 없는 함수는
길이를 추정할 수 없어 기존 무통신 간격 방식으로 돌아갑니다.
이 덕분에 미지원 함수에 대한 서버의 예외 응답 동작을 유지합니다.
길이 모드에서 연속 프레임은 첫 프레임 길이만 소비하며, 유효한 프레임 뒤의
추가 데이터는 다음 수신에 남습니다. 기존 모드처럼 전체 버스트를 하나로 묶어
거부하지 않으므로 동작 차이를 이해한 뒤 선택해야 합니다.
잘못된 길이/CRC 또는 타임아웃 후에는 잔여 데이터 정리가 여전히 필요합니다.

### 디버거 없는 실행의 지연 분석

같은 앱과 배선에서 디버거 프로토콜 명령을 보내지 않고 COM3 USB 출력만
읽는 대조 시험을 수행했습니다. COM6는 동일한 9600 8E1, RTS/DTR 활성 상태를
유지했습니다. 저장값을 변경하지 않는 Holding 1개 읽기와 손상 CRC 요청만
사용했습니다.

| 조건 | 관측 결과 |
|---|---|
| COM3 닫힘 | 최초 읽기 및 CRC 오류 후 읽기가 각각 5초간 무응답 |
| COM3 닫힘, 추가 12초 대기 | 정상 읽기가 다시 성공: 약 63~90ms |
| COM3 열고 출력만 읽기, 디버거 명령 없음 | 최초/12초 유휴 후/CRC 오류 후/추가 12초 후 읽기가 모두 약 46~62ms |

USB 읽기 조건의 재시험에서는 약 30초 동안 12,002바이트의 Wire Protocol
출력을 받았고, `TimeoutException` 스택 16개와 CRC 관련
`ModbusProtocolException` 스택 1개가 포함됐습니다.
Release 및 `SerialDiagnostics=false`에서도 CLR 자체의 예외 출력은 발생했습니다.
따라서 이 현상은 디버거가 통신 로직을 대신 실행하는 것이 아니라,
USB 진단 출력이 소비될 때 처리 지연이 사라지는 현상입니다.
영구 정지로 단정했던 무응답 중 일부는 수초 지연 후 정상 복귀했습니다.

공개 펌웨어 소스에서 다음 경로를 확인했습니다.

- [`System_Exception::SetStackTrace`](https://github.com/nanoframework/nf-interpreter/blob/103df2a60c833934e4cafe2b38385f39537c265a/src/CLR/CorLib/corlib_native_System_Exception.cpp): 예외의 스택을 설정할 때 `NANOCLR_TRACE_EXCEPTIONS` 조건에서 예외/스택을 출력합니다. catch 이전에도 발생합니다.
- [`CLR_Debug::Emit`](https://github.com/nanoframework/nf-interpreter/blob/103df2a60c833934e4cafe2b38385f39537c265a/src/CLR/Diagnostics/Info.cpp): 각 출력 줄을 디버거 Wire Protocol 메시지로 전달합니다.
- [`WP_TransmitMessageUsbJtag`](https://github.com/nanoframework/nf-interpreter/blob/103df2a60c833934e4cafe2b38385f39537c265a/targets/ESP32/_common/WireProtocol_HAL_Interface.c): 헤더/본문 USB 쓰기에 각각 250ms 대기를 전달합니다. 출력 줄마다 누적될 수 있습니다.

수정 전 코드에서는 정상적인 서버 유휴 대기도 제한 시간마다 예외를 만들었습니다.
당시 `SerialPortChannel.Read`는 `OperationDeadline.RemainingMilliseconds`의
`TimeoutException`을 잡아 0을 반환하고, `ChannelTransport.ReadExactly`는
다시 `TimeoutException`을 던집니다. 한 번의 유휴 만료가 두 번의 예외 출력으로
이어질 수 있습니다. CRC 오류도 예외/스택 출력 후에야 샘플의 catch 및
`Resynchronize`에 도달합니다. 출력 지연 중 도착한 요청은 이후 수신 버퍼
정리에서 폐기될 수 있습니다.

**재현한 오류 후 무응답의 주요 원인은 CLR 예외 출력에 따른 USB 송신
지연으로 좁혀졌습니다.** USB 드라이버의 내부 대기 시간을 직접 계측한 것은
아니며, 예전의 모든 간헐 장애나 전기적 문제까지 설명한다고 단정하지 않습니다.
COM3를 계속 읽는 것은 진단용 대조 조건이지 제품의 해결책이 아닙니다.
해결 방향은 정상 유휴/예상 가능한 잘못된 프레임을 예외 없이 상태로 보고하는
수신 경로와 오류 카운터/최근 오류 기록을 제공하는 것입니다. 실제 I/O 및
프로그래밍 오류는 계속 명시적으로 전파해야 합니다.
기존 예외 API를 단순 catch로 감싸거나 앱의 `Debug.WriteLine`만 끄는 방식은
CLR의 예외 발생 시 출력을 제거하지 못합니다. 아래 상태 경로로 이 구조를 수정했습니다.

대조 시험 중 COM3에 COM6용 RTS/DTR 설정을 적용한 재실행 한 번은
`ESP-ROM:` 메시지를 내며 ROM 상태에 들어갔으므로 유효한 측정에서 제외했습니다.
플래시 쓰기 없이 하드웨어 리셋으로 기존 CLR/앱을 복구한 뒤,
COM3의 RTS/DTR을 모두 비활성화한 조건에서 위 USB 읽기 결과를 재확인했습니다.
USB Serial/JTAG의 제어선은 MCU를 리셋할 수 있으므로 COM6 컨버터의 설정과
혼동하지 마십시오. 이 대조 시험 단계에서는 보드 펌웨어와 앱을 변경하지 않았습니다.

### 상태 기반 수신 적용

네이티브 펌웨어, 배선, 종단저항 상태는 변경하지 않고 관리 앱과 라이브러리만
수정했습니다. 기존 예외 API는 유지하며 RTU/ASCII 루프는 `TryProcessNextRequest`를
사용합니다. 유휴, 부분 수신 타임아웃, CRC/길이/Unit 오류는 `ReceiveStatus`로
보고하므로 해당 경로에서 CLR 예외 출력을 유발하지 않습니다.
`SerialPortChannel`의 빈 버퍼 폴링도 만료 시 직접 0을 반환합니다.
잘못된 호출 인수나 실제 I/O 장애, 잘못된 채널 반환 수량은 숨기지 않습니다.
TCP 및 기존 예외 수신 API의 예외 계약은 변경하지 않았습니다.
Modbus 요청 검증/저장소 오류를 예외 응답으로 바꾸는 기존 경로는 여전히
`ModbusException`을 사용할 수 있으므로 모든 오류가 무예외가 된 것은 아닙니다.

샘플은 `Idle`에 수신 버퍼 정리를 하지 않습니다. 부분 수신/프레임 오류에만
기존 50ms 관측 무통신 재동기화를 적용하고, 다음 Input Register로 결과를
보고합니다. 주소는 0 기반이며 각 카운터는 65536에서 순환하고 앱 재시작 시
초기화합니다. 최근 오류는 성공/유휴 상태로 지워지지 않습니다.

| Input Register | 의미 |
|---|---|
| 120 | 유효 프레임 수신 횟수 (다른 Unit/무응답 브로드캐스트 포함) |
| 121 | 수신 바이트가 없는 유휴 제한 시간 만료 횟수 |
| 122 | 부분 프레임 타임아웃 횟수 |
| 123 | 체크섬 오류 횟수 (RTU CRC / ASCII LRC) |
| 124 | 그 외 프레임 오류 횟수 |
| 125 | 최근 오류의 `ReceiveStatus` 값: Timeout=2, InvalidLength=3, FrameTooLong=4, CrcMismatch=5, InvalidUnitId=6, LrcMismatch=7, InvalidCharacter=8 |

카운터는 요청 처리 또는 오류 재동기화가 끝난 뒤 갱신하므로, 카운터를
읽는 응답은 그 읽기 요청 자체가 증가시키기 전의 값입니다.
Input 120..125는 이제 진단용이며 항상 0인 입력으로 간주하면 안 됩니다.
Holding 영역의 주소/초기값과 쓰기 동작은 그대로입니다.

**COM3 닫힘, 디버거 없음, USB 출력 수신 프로그램 없음** 조건에서 확인했습니다.

| 항목 | 수정 후 결과 |
|---|---|
| 최초/12초 유휴 후/CRC 오류 후/추가 12초 후 1 Register 읽기 | 모두 약 61~72ms 정상 응답 |
| 길이 경계 및 최대 쓰기 | 119/120/121/239/240/241/255바이트, 최대 Coil/Register 쓰기와 되읽기 성공 |
| 최대 크기의 비정상 요청 | 256바이트 처리 및 예외 03 응답 |
| 기능 | 8개 함수, 브로드캐스트 4종, 주소/수량/함수 예외 응답 성공 |
| 최대 읽기 반복 | 125 Register 100회, 45.9초, 데이터/CRC 모두 일치 |
| 유휴 복구 | 12초 대기 후 정상 최대 읽기 |
| 부분 프레임 복구 | 4바이트 부분 요청의 전체 수신 타임아웃 후 정상 최대 읽기 |
| CRC 복구 및 가시성 | 손상 CRC 10회 무응답/정상 최대 읽기 복구, CRC 카운터 증가량 10 및 최근 오류 상태 일치 |
| 종료 상태 | 전체 백업 데이터 복원/비교 성공, 복원 파일 제거 |

PC 회귀는 12,667개 검증을 통과했습니다. 신규 상태 반환 수신, 코덱,
유휴 UART 폴링 및 서버 루프에 최초 예외 발생 감시를 적용해 예상 실패에서도
내부 예외가 0회임을 확인합니다. 실제 I/O/잘못된 인수 예외 전파와 기존
예외 수신 API의 동작도 검증합니다.
재현한 유휴/CRC 오류 후 지연은 해결됐지만, 과거 모든 간헐 장애의 원인이나
장시간 안정성 및 전기적 신호 품질을 보장하는 결과는 아닙니다.

### UART 배치 전달 원인 조사

보드 정보에서 CLR 1.18.0.30, 빌드 날짜 2026-10-21, ESP-IDF v5.5.6을 확인했습니다.
해당 날짜 공개 소스
[`nf-interpreter` 103df2a의 UART 드라이버](https://github.com/nanoframework/nf-interpreter/blob/103df2a60c833934e4cafe2b38385f39537c265a/targets/ESP32/_nanoCLR/System.IO.Ports/sys_io_ser_native_System_IO_Ports_SerialPort.cpp)는
`BytesToRead`에 관리 링 버퍼 길이만 반환합니다. UART 이벤트가 발생해야
ESP-IDF 수신 데이터를 이 링 버퍼로 옮기며, 하드웨어 FIFO 수신 임계값과
유휴 타임아웃을 별도로 재설정하지 않습니다.
[`ESP-IDF` v5.5.6](https://github.com/espressif/esp-idf/blob/v5.5.6/components/esp_driver_uart/src/uart.c)의
기본 FIFO 채움 임계값은 120바이트, RX 타임아웃은 10심볼입니다.
흐름 제어용 `rx_flow_ctrl_thresh`와 FIFO 채움 임계값은 서로 다른 설정입니다.
`ReceivedBytesThreshold`, `ReadBufferSize`, `ReadTimeout`을 바꾸는 것으로
이 하드웨어 임계값이나 실제 프레임 경계 정보를 설정할 수는 없습니다.

프로토콜 처리를 중단한 진단 앱에서 각 입력을 한 번의 PC Write로 보냈습니다.
수신 중 USB 출력을 하지 않고, 읽기 시각과 수량을 고정 배열에 저장했다가
관리 버퍼가 1초간 비어 있는 것이 관측된 뒤 출력했습니다.
아래는 디버거 연결 상태에서 첫 배치 대비 관리 코드 관측 시각입니다.
시계 해상도와 스케줄링이 포함된 값이며 전선상의 바이트 간격은 아닙니다.

| PC 송신 길이 | MCU 배치 수량 | 첫 배치 이후 관측 시각(ms) |
|---|---|---|
| 8 / 119 / 120 | 각각 한 배치 | 0 |
| 121 | 120 + 1 | 0 / 10 |
| 239 | 120 + 119 | 0 / 150 |
| 240 | 120 + 120 | 0 / 140 |
| 241 | 120 + 120 + 1 | 0 / 140 / 150 |
| 255 | 120 + 120 + 15 | 0 / 140 / 160 |
| 256 | 120 + 120 + 16 | 0 / 130 / 160 |

모든 입력에서 총 수신 바이트 수가 송신 수와 일치했습니다.
이 진단은 수신 수량과 전달 간격을 측정하며 내용 일치나 CRC를 검증하지 않습니다.
9600 8E1에서 120바이트 전송에는 약 137.5ms가 걸리므로,
다음 FIFO 채움 이벤트까지 기다리는 배치 전달과 관측 결과가 부합합니다.
기존 `SilentInterval` 모드의 5ms 관리 버퍼 대기는 이를 프레임 종료로 오인합니다.
종단저항이 없는 기존 배선을 그대로 사용했으며, 신호 품질에 대한 검증은 아닙니다.
간헐 무응답 및 디버거 없는 복구 문제까지 같은 원인이라고 단정하지 않습니다.

측정을 재현하려면 `/p:SerialReceiveProbe=true`로 Rebuild해 앱을 배포합니다.
**이 모드에는 Modbus 서버가 없고 직렬 응답을 보내지 않습니다.**
COM3 USB 디버그 출력을 연결하고 COM6 9600 8E1, RTS/DTR 활성 상태에서
각 입력을 보내되 2초 이상 간격을 두십시오. 한 측정은 최대 32개 배치를
기록하며 이를 넘으면 예외를 보고합니다. 1초 대기는 진단 기록을 나누기 위한
기준일 뿐 RTU 프레임 판정에 사용되지 않습니다.
시험 후에는 `/p:SerialReceiveProbe=false /p:SerialDiagnostics=false`로
Rebuild하고 일반 앱을 다시 배포해야 합니다.
이번 측정 후에는 일반 RTU 서버로 복원하고 기본 읽기 응답까지 확인했습니다.
보드의 네이티브 펌웨어는 변경하지 않았습니다.

## 확장 시험과 복원 명령

`-Extended`는 쓰기 시험을 포함합니다. 실제 설비에는 실행하지 마십시오.
Holding 0..124와 Coil 0..1999를 읽어 `bin\serial-backup.clixml`에 저장한 뒤
시험하며, 종료 시 Holding은 10개, Coil은 최대 160개씩 나눠 복원하고
전체 백업 영역을 비교합니다. 복원에 실패하면 백업 파일을 남기며,
시험 오류와 복원 오류를 모두 보고합니다.
기존 백업은 덮어쓰지 않습니다.

```powershell
.\Samples\Modbus\VagabondK.nF.Modbus.SerialServer\Test-SerialServer.ps1 -PortName COM6 -Extended -Iterations 100
.\Samples\Modbus\VagabondK.nF.Modbus.SerialServer\Test-SerialServer.ps1 -PortName COM6 -Restore
```

현재 길이/상태 기반 모드에서는 위 명령 전체를 COM3를 닫은 상태로 통과했습니다.
명령은 유휴 12초, 부분 프레임 타임아웃, CRC 오류 10회 복구 및 진단 카운터도
검사합니다. 예기치 않은 실패 시에는 복원 파일이 남았는지 반드시 확인하십시오.
복원 전 파일 버전, 포트, 통신 설정, Unit ID 및 데이터 길이/형식을 검사합니다.
포트 이름만으로 실제 장치의 신원을 확인할 수는 없으므로 동일한 시험 보드와
앱이 연결되어 있는지 먼저 확인하십시오. 이전 형식의 백업은 자동 복원하지 않습니다.
기본 읽기 명령은 초기 Holding 값 123,456,65535를 기대합니다.

CS-428/9AT-mini3는 자동 방향 전환 및 RS232 신호선 전원 방식입니다.
제조사 매뉴얼 기준 DIP 1 ON, 2/3/4 OFF는 RS485 2선식,
Non-Echo, 종단저항 미사용입니다. TRX+는 DM2472 D+/A,
TRX-는 D-/B에 연결하며, 이 컨버터의 2선식 단자에는 별도 GND가 없습니다.
다른 제품의 A/B 이름만으로 극성을 판단하지 마십시오.

`/p:SerialDiagnostics=true`를 추가해 Rebuild하면 USB 디버그 출력에
수신/송신 바이트를 최대 32개 기록합니다. 이 옵션은 타이밍과 메모리에
영향을 주므로 고립된 요청의 진단에만 사용하고 성능 시험에서는 끄십시오.

`/p:SerialProtocol=Ascii`를 추가해 Rebuild하면 ASCII 9600 7E1 앱을 만듭니다.
PC 스크립트도 `-Protocol Ascii`로 실행해야 합니다.

### ASCII 실물 시험

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' .\Samples\Modbus\VagabondK.nF.Modbus.SerialServer\VagabondK.nF.Modbus.SerialServer.nfproj /t:Rebuild /p:Configuration=Release /p:SerialProtocol=Ascii /v:minimal /nologo
nanoff --nanodevice --deploy --serialport COM3 --image .\Samples\Modbus\VagabondK.nF.Modbus.SerialServer\bin\Release\VagabondK.nF.Modbus.SerialServer.bin --suppressnanoffversioncheck
.\Samples\Modbus\VagabondK.nF.Modbus.SerialServer\Test-SerialServer.ps1 -PortName COM6 -Protocol Ascii -Extended -Iterations 100
```

2026-10-23, 기존 배선/펌웨어/종단저항 없는 구성에서 COM3와 디버거를 닫고
아래 결과를 확인했습니다. 시험 전 데이터를 백업하고 종료 시 전체 영역을
복원/비교했으며, 성공 후 복원 파일도 제거했습니다.
복원 파일에는 프로토콜을 기록하며 다른 프로토콜로 복원하는 호출은 거부합니다.
기존 프로토콜 필드 없는 복원 파일은 RTU로 취급합니다.

| 항목 | 결과 |
|---|---|
| 기본 통신 | PC/MCU 모두 9600 7E1, Unit 1, 초기 Register 값 일치 |
| 8개 함수 | 최대 읽기, 단일 쓰기, 최대 1968 Coil / 123 Register 쓰기 및 되읽기 |
| 긴 요청 | Coil 쓰기 ADU 239/241/243/479/481/483/511바이트 성공 |
| 최대 프레임 | Coil 바이트 수 필드가 잘못된 513바이트 요청을 수신하고 예외 03 응답 |
| 반복 | 125 Register 읽기 100회, 70.8초, 데이터/LRC 모두 일치 |
| 브로드캐스트/예외 | 쓰기 4종 무응답 및 반영, 주소/수량/함수 예외 응답 |
| 유휴/부분 수신 | 12초 유휴 및 부분 요청 만료 후 정상 최대 읽기 |
| LRC 오류 | 10회 거부/복구 및 체크섬 진단 카운터 증가량 10 확인 |
| 기타 수신 오류 | 잘못된 HEX, CRLF, 513바이트 초과(514바이트), 문자 간 1초 만료를 거부하고 상태/카운터 확인 |
| 재시작/허용 지연 | 부분 프레임 뒤 새 `:`로 재시작, 300ms 분할 전송 정상 수신 |

예상 가능한 ASCII 수신 오류와 유휴는 내부에서 예외를 만들지 않고 상태로
반환합니다. 요청 PDU/저장소가 만드는 Modbus 예외 응답, 실제 I/O 오류는
기존 경로를 유지합니다. ASCII Client는 여전히 예외 기반입니다.
최초 바이트 전에는 전체 3초를 대기하고, `:` 이후에는 각 Read 대기를
최대 1초로 제한합니다. 재시작 `:`도 전체 3초를 연장하지 않습니다.
이는 관리 버퍼에서 관측한 간격이며 전선상의 정확한 문자 간격 계측은 아닙니다.
원본 로그는 `bin\ascii-server.log`입니다.
`-Endurance`는 현재 RTU 전용이며 ASCII에서는 명시적으로 거부합니다.

수신 타임아웃/프레임 오류 후에는 수신 데이터를 버리고 관측 가능한
50ms 무통신 구간을 기다립니다. 이때 보낸 요청은 유실될 수 있습니다.
RTU의 `Idle` 상태에는 이 정리를 적용하지 않고 다음 수신으로 진행합니다.

### 장시간 읽기 및 오류 복구 시험

COM3와 디버거를 닫고 검증된 PC 어댑터 COM6에서 실행합니다.
Windows PowerShell 5.1 또는 `System.IO.Ports`를 로드한 PowerShell이 필요합니다.

```powershell
.\Test-SerialServer.ps1 -PortName COM6 -Endurance -EnduranceSeconds 7200
```

최대 125 Holding Register 읽기를 반복하며 시작 시와 각 오류 시험 종료 후
60초마다 CRC 오류, 부분 프레임 타임아웃, 12초 유휴 후 복구를 검사합니다.
각 오류 뒤 전체 Register 데이터를 비교하고 수신 진단 카운터도 검사합니다.
Coil 2000개는 각 오류 주기와 종료 시 처음 읽은 값과 비교합니다.
쓰기 요청이나 앱 재배포는 하지 않으며 COM3를 열지 않습니다.
데이터/CRC/카운터 불일치나 4초 응답 제한 초과는 즉시 실패로 기록하고 종료합니다.
시험 도중 다른 Master나 도구가 COM6 또는 같은 버스를 사용하면 안 됩니다.

`bin\endurance-날짜-시간.csv`에 시작, 주기별 복구 시간/카운터, 진행,
최종 `Passed` 또는 `Failed`를 기록합니다. `-LogPath`로 경로를 지정할 수 있고
기존 로그는 덮어쓰지 않습니다. 시간은 PC에서 잰 값이며 요청 전 100ms 간격과
최대 응답 전송 시간을 포함하므로 1 Register 응답 시간과 직접 비교하면 안 됩니다.
지정 시간에 도달하면 진행 중인 오류 주기를 마친 뒤 최종 데이터를 비교하므로
종료가 수십 초 늦어질 수 있습니다. 강제 종료로 최종 상태가 없으면 미완료입니다.
현재 배포 앱에는 MCU 힙 측정값이 없으므로 이 시험은 메모리 누수 판정이나
엄밀한 RTU 타이밍/전기적 품질 검증을 제공하지 않습니다.

2026-10-23 12:45:34~14:45:35 KST에 위 2시간 시험을 완료했습니다.
COM3를 닫고 기존 배선/펌웨어/종단저항 없는 상태를 유지했습니다.
최대 Register 읽기 반복 11,901회와 오류/유휴 복구 주기 91회가 모두
성공했고 실패 기록은 0개였습니다. 각 주기에 CRC 오류 1회, 부분 프레임
타임아웃 1회, 12초 유휴를 포함하며 진단 카운터 증가량도 검증했습니다.
종료 시 Holding Register/Coil은 시작 데이터와 일치했습니다.

| PC 측정 항목 | 평균 | 최대 |
|---|---|---|
| CRC 오류 후 최대 읽기 | 457.0ms | 482.3ms |
| 부분 프레임 타임아웃 후 최대 읽기 | 458.2ms | 477.9ms |
| 12초 유휴 후 최대 읽기 | 456.5ms | 486.8ms |

일반 최대 읽기 반복의 최장 시간은 511.8ms였습니다. 위 시간은 오류 대기
자체를 제외한 복구 요청의 소요 시간이며 요청 전 100ms 간격을 포함합니다.
원본 기록은 `bin\endurance-20261023-2h.csv`입니다.
단일 Master에서 요청을 직렬화하고 충분한 대기 후 다시 시도하십시오.
정확한 RTU t1.5/t3.5 감지와 고속 통신 적합성은 아직 보장하지 않습니다.
