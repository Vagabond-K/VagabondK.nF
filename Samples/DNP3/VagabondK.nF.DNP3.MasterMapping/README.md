# DNP3 Master 수신 매핑 MCU 샘플

ESP32-S3가 TCP 또는 RS-485 Serial Master로 원격 Outstation을 읽고, 7종 Point의 값·품질·시각을
앱 모델에 반영하는 하드웨어 샘플입니다. 리플렉션이나 새 MCU 런타임 DLL은 사용하지 않습니다.
PC 빌드 전용 생성기와 기존 코어·Master·선택한 전송 프로젝트만 참조합니다.

## 실행과 모델 사용

`Program.cs`는 보드에 저장된 Wi-Fi 설정을 사용합니다. 유효한 IP를 최대 90초 기다린 뒤
PC `192.168.0.210:20000`에 연결합니다. 로컬 Master 주소는 2, 원격 Outstation 주소는 1입니다.
`ReadClasses(15)`로 3초 간격 조회하며 통신 오류를 기록하고 새 소켓으로 재접속합니다.
코드에 SSID·암호를 넣거나 펌웨어·네트워크 설정을 변경하지 않습니다.

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' `
  .\Samples\DNP3\VagabondK.nF.DNP3.MasterMapping\VagabondK.nF.DNP3.MasterMapping.nfproj `
  /t:Build /p:Configuration=Release /warnaserror
```

`ReceivedData.cs`는 Binary, DoubleBitBinary, Counter, FrozenCounter, Analog,
BinaryOutputStatus, AnalogOutputStatus 각각의 index 0을 매핑합니다.
값과 각 Point의 Flags·Timestamp는 private setter 속성으로 노출합니다.

`FloatFormats.cs`는 Analog/AnalogOutputStatus의 float32 대입도 보여줍니다.
일반 TCP/Serial 샘플은 `ReceivedData.Observer`에 이 추가 바인딩을 연결해 인덱스 1을 별도 모델에 반영합니다.
두 모델 모두 같은 `sync`를 사용합니다. 기존 실물 전용 시험기의 7종/인덱스 0 결과와 추가 형식의 PC 검증은 구분합니다.
모든 지원 형식·역할은 [대조표](../../MappingCoverage.md)에 정리했습니다.

```csharp
var data = new ReceivedData();
var sync = new object();
var binding = new ReceivedData.Dnp3MasterBinding(data, sync);
var master = new Dnp3Master(2, 1, binding, maximumMeasurements: 64);

lock (sync)
{
    double value = data.Temperature;
    byte quality = data.TemperatureFlags;
    long timestamp = data.TemperatureTime;
}
```

`maximumMeasurements`는 매핑 수가 아니라 **한 Application Fragment의 전체 수신 개수**입니다.
미등록 Point도 포함합니다. 이 7종 샘플의 독립 피어는 총 56개를 사용하므로 64를 지정합니다.
규모 계측 피어에는 490개가 있으므로 기본값과 같은 512를 지정합니다.
원격 Fragment 구성을 확인하지 않고 모델 크기에 맞춰 이 용량을 줄이지 마십시오.

필수 `[Dnp3Received]` 메서드는 갱신 후 원본 배열과 `Dnp3ResponseInfo`를 받습니다.
샘플은 Fragment·이벤트·미등록 index 개수를 기록하고, 시험에서는 Observer로 원본도 검증합니다.
같은 Point의 이벤트가 여러 개면 **모델에는 마지막 수신값**, 원본 배열에는 모든 이벤트가 남습니다.
개별 이벤트 값은 모델이 아니라 배열에서 읽으십시오. Unsolicited와 빈 Fragment도 콜백 대상입니다.
모델 초기값은 아직 수신하지 않은 상태이지 원격 장치의 확인된 값이 아닙니다.

앱도 같은 `sync`를 사용해야 값·품질·시각을 일관되게 읽을 수 있습니다.
사전 검증·대입·콜백은 하나의 잠금 안에서 실행됩니다. setter/콜백을 짧게 유지하고
네트워크 호출이나 같은 Master의 재진입을 피하십시오. 각 Fragment 단위 갱신이며,
여러 Fragment 전체의 원자성이나 타임스탬프 순 정렬은 제공하지 않습니다.
Flags와 시각은 디코딩 원본 그대로입니다. 시각 없는 Static Variation의 0도 그대로 저장합니다.

사전 값 검사 실패는 대입 전에 발생합니다. 사용자 setter/콜백 예외는 롤백하지 않고 전파하며,
해당 `Process` 호출은 Confirmation을 반환하지 않습니다. 앱 예외를 성공으로 취급하지 마십시오.
재연결/재전송을 포함한 영속적인 exactly-once 처리는 보장하지 않습니다.
지원 형식과 모든 옵션은 [Generator 계약](../../../Source/DNP3/VagabondK.nF.DNP3.Mapping/README.md)을 참조하십시오.

## 독립 피어와 실물 재실행

먼저 `Build\Build-Dnp3ValidationMaster.ps1`로 원본 OpenDNP3 검증 도구를 준비합니다.
시험 스크립트는 원본 라이브러리의 `DECODER ORIGINAL` 식별자, COM3,
원래 앱 복원 이미지의 SHA256을 확인하고 PC 회귀와 네 개 MCU 이미지를 모두 빌드한 뒤 배포합니다.

```powershell
.\Build\Test-Dnp3MasterMappingHardware.ps1 -AllowDeviceChanges `
  -OutputPath .\bin\dnp3-master-mapping-new
```

Geekble Nano ESP32-S3 `192.168.0.187`/COM3와 PC `192.168.0.210`의 격리 평가 환경 전용입니다.
TCP 20000은 PC Outstation, UDP 20002는 MCU 시험 결과 수집용입니다.
배포는 실패 로그 후 최대 3회, 복원은 실패 시 한 번 재시도합니다.
finally에서 원래 Modbus 앱을 복원하고 COM6/RTU 9600 8E1의
holding 0..2 = 123/456/65535를 확인합니다. `-Points 128`처럼 한 이미지만 선택할 수도 있습니다.
출력 경로는 기존 결과를 덮어쓰지 않는 새 폴더여야 합니다.

최종 `bin\dnp3-master-mapping-verified` 실행에서 샘플 **9,009개 검증**,
이벤트 **1,344개**, 그중 Unsolicited **448개**, 전체 수신 **1,456개**를 확인했습니다.
원본 Outstation의 56개 Point 모두를 수신하며 모델에 없는 index 1~7도 콜백에서 검사합니다.
두 연결에 걸친 Static·반복 index 이벤트의 순서·값·품질·시각,
Direct/Select+Operate 후 이벤트, Confirmation 후 드레인 및 비요청 수신을 확인했습니다.
최종 실행은 첫 복원 시도와 COM6 읽기까지 통과했습니다.

## 8/32/128 Point 자원 측정

각 규모는 7종 Point를 순환 배치합니다. 값은 필드, 품질은 필드와 setter-only 속성을 교차 사용하고,
시각은 배열 기반 setter-only 속성을 사용합니다. 이를 통해 실제 두 대입 경로를 확인하면서
nanoFramework의 형식당 필드·메서드 수 제한을 지킵니다.
배열은 모델 생성 시 할당하며 정상 수신 경로에 새 저장소를 만들지 않습니다.

독립 Outstation은 종류별 70개, 총 **490개 Static**을 **7개 Fragment**로 전달합니다.
모든 수신 개수와 8/32/128개 모델의 값·품질·시각을 검사하고, 원본 배열과 응답 정보를 보관합니다.
연결을 닫고 1회 준비 후 같은 490개 전체를 20회 재생해, 총 9,800개 레코드를 처리합니다.
미등록 레코드도 매 순회 처리하므로 아래 시간은 한 Point 처리 시간이나 통신 RTT가 아닙니다.

| 매핑 Point | 모델 RAM | 바인딩·잠금 객체 RAM | 20회 재생 합계 | GC 잔류 증가 | 계측 포함 앱 PE | 전체 배포 이미지 |
|---:|---:|---:|---:|---:|---:|---:|
| 8 | 348 B | 48 B | 3,330 ms | 0 B | 15,344 B | 110,548 B |
| 32 | 984 B | 48 B | 3,790 ms | 0 B | 22,924 B | 118,128 B |
| 128 | 3,528 B | 48 B | 4,880 ms | 0 B | 53,884 B | 149,088 B |

모델 RAM은 값·품질·시각 배열과 계측용 카운터·참조를 포함한 생성 전후 GC 차이입니다.
바인딩과 공유 잠금 객체는 합계 48 B이며 별도 Point 캐시나 이벤트 큐를 만들지 않습니다.
원본 배열을 보관하는 32슬롯 가변 배열과 응답 정보 배열은 **별도 3,504 B**이고,
이들이 참조하는 수신 측정값 배열·Master 버퍼·TCP/디코더 메모리는 위 모델·바인딩 RAM과 별개입니다.
재생 전·직후·최종 GC의 가용 힙은 각각 같은 값이었습니다.
이는 최대 사용량이나 네이티브·네트워크 RAM을 측정했다는 뜻도, 전체 통신에 할당이 없다는 뜻도 아닙니다.
PC에서는 생성 매핑의 정상 수신 10,000회에 추가 관리 메모리 할당 0 B도 별도로 확인합니다.

core PE는 10,596 B, Master PE는 6,228 B입니다.
7종 실제 검증 앱은 PE 10,944 B/전체 배포 106,148 B입니다.
표의 앱 PE에는 생성 바인딩 외에도 전체 값 검사·실패 문자열·UDP 진단·원본 캡처·계측 코드가 포함됩니다.
순수 생성 코드 증가량이나 운영용 샘플 크기로 해석하지 마십시오.
MCU 타이머 해상도는 약 10ms이며, 위 관측값은 보장된 실시간 처리 지연이 아닙니다.
`sizes.csv`, 각 규모의 `measurements.json`, 피어·배포·복원 로그에 근거를 보존합니다.

## 준비 중 발견한 실패와 수정

- 첫 실행의 `Measure` 함수는 PowerShell 기본 별칭과 충돌했습니다. `Wait-MasterMetrics`로 변경했습니다.
- 다음 8 Point 시험은 수신 용량 64가 원격 Fragment를 수용하지 못했습니다.
  모델 개수와 수신 용량을 분리해 512로 명시했습니다. 값 검사를 우회하거나 재요청으로 숨기지 않았습니다.
- 초기 128 Point 모델은 값·품질·시각을 모두 개별 필드에 두어 인스턴스 필드가 **389개**였습니다.
  nanoFramework 타입 메타데이터의 `iFields_Num`은 UInt8이므로 유효 범위는 255개까지입니다.
  490개 수신은 완료됐지만 Point 43 이후 모델 값이 0이었고, 원본의 같은 Point는 정상 값 1이었습니다.
  진단 응답으로 원본/모델을 비교해 수신 손실이 아닌 잘못된 모델 레이아웃임을 확인했습니다.
  품질·시각 저장소를 위 구조로 바꾸고, Master Generator에 필드/메서드 초과 진단 **VKDN007**을 추가했습니다.
  자동 속성의 내부 필드·접근자와 생성자도 계산하므로 큰 모델은 배열 기반 속성이나 별도 모델로 구성해야 합니다.
  생성 분기는 nanoCLR에서 switch 분기 수를 1바이트로 표현하는 한도도 넘지 않도록 분할합니다.

실패한 실행도 원래 앱 복원과 COM6 읽기를 확인했습니다.
실패 응답 `DMME`는 단계·예외·Point별 불일치 정보를 보존하며 시간 초과나 성공 결과로 바꾸지 않습니다.
필드 제한 근거는 [nanoCLR 타입 정의](https://github.com/nanoframework/nf-interpreter/blob/main/src/CLR/Include/nanoCLR_Types.h)의
`CLR_RECORD_TYPEDEF`입니다. MCU 펌웨어나 기존 Master 프로토콜 런타임을 수정한 해결이 아닙니다.

TCP Master의 단기 실물 검증과 아래 8시간 반복 수신·장애 복구 시험,
Serial Master의 단기 실물 수신·장애 복구 시험을 수행했습니다.
Serial 장시간 시험의 절차는 아래에 별도로 설명하며,
전원 차단 내구성이나 인증/산업용 적합성 검증은 포함하지 않습니다.

## RS-485 Serial 실행과 장애 복구

같은 프로젝트에 `/p:Dnp3SerialMapping=true`를 지정하면 `SerialProgram.cs`가 진입점입니다.
기본 TCP 경로와 출력 폴더를 분리하며, 결과는 `bin\Release\Serial`에 생성합니다.
Serial 샘플에는 core·Master·Serial과 UART에 필요한 MCU DLL만 포함하고,
TCP·Outstation·System.Net 또는 PC Generator DLL은 배포하지 않습니다.
보드에 저장된 Wi-Fi 설정을 사용하지 않는 UART 전용 샘플입니다.

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' `
  .\Samples\DNP3\VagabondK.nF.DNP3.MasterMapping\VagabondK.nF.DNP3.MasterMapping.nfproj `
  /t:Build /p:Configuration=Release /p:Dnp3SerialMapping=true /warnaserror
```

Geekble Nano ESP32-S3의 COM2/UART2, GPIO17 TX·GPIO18 RX를
DM2472 자동 방향 RS-485 모듈에 연결합니다. 통신 설정은 **9600 8E1**입니다.
DM2472가 방향을 제어하므로 MCU UART는 **Normal 모드**를 유지하고
RTS 기반 RS485 네이티브 모드를 설정하지 않습니다.
Master 주소 2에서 Outstation 주소 1로 3초 간격 `ReadClasses(15)`를 요청합니다.
모델·바인딩·Master는 유지하고, I/O·시간 초과·프로토콜 오류는 기록한 뒤
포트를 폐기하고 3초 후 새 포트·세션을 열어 `Reset`합니다.
새 세션에서의 재수신 예제이며, 사용자 setter/콜백 오류를 복구하는 정책은 아닙니다.

### 독립 피어를 이용한 실물 재실행

```powershell
.\Build\Build-Dnp3ValidationMaster.ps1
.\Build\Test-Dnp3MasterMappingSoak.ps1 -AllowDeviceChanges -SerialValidation `
  -OutputPath .\bin\dnp3-master-serial-mapping-new
```

`-SerialValidation`만 지정하면 시간 기반 TCP 시험 대신 **정상 5순회·장애 3회**의 고정 절차를 실행합니다.
`DurationMinutes` 등 반복 시간 옵션은 이 고정 모드에서 사용하지 않습니다.
원본 OpenDNP3 피어는 PC COM6에서 `COM6 soak`로 실행하며, MCU와 같은 9600 8E1입니다.
한 정상 순회는 한 시리얼 세션에서 8라운드를 수행하여
1,400개 측정값·1,344개 이벤트·448개 Unsolicited를 검사합니다.
7종 Point의 값·품질·시각, 중복 index 이벤트, 미등록 Point 콜백과
Confirmation 후 Link ACK까지의 드레인을 확인합니다.

실물 시험 이미지는 공유 TCP/Serial 검증기와 UDP 20002 제어를 재사용하므로
TCP DLL·Wi-Fi도 필요합니다. 위 UART 전용 운영 샘플과는 구성이 다릅니다.
명령 제한 시간은 300초이며, 같은 UDP 순번 재전송은 실행을 중복하지 않습니다.

| 주입 장애 | 확인 조건과 복구 |
|---|---|
| 피어 종료 후 Confirmed Link ACK 미수신 | 1초 ACK 제한과 2회 재전송 후 Timeout·Faulted를 확인하고, Reset·피어 재시작 후 정상 수신 |
| Unconfirmed Link에서 Application 응답 미수신 | Link 재전송 없이 10초 응답 Timeout·Faulted를 확인하고, Reset·피어 재시작 후 정상 수신 |
| 실제 UART에 `05 64 14` 3바이트만 주입 | 1초 뒤 FrameTimeouts=1·DiscardedBytes=3·CRC 오류 0, 콜백 증가 없음·Faulted 아님을 확인하고 정상 피어 수신 재개 |

부분 프레임 시험은 피어 종료 후 PC가 COM6를 미리 연 다음 MCU를 준비시키고 주입합니다.
포트 소유권 변경 시 AccessDenied는 경고와 함께 최대 3회만 재시도하고,
다른 예외나 재시도 소진은 실패로 전파합니다.
첫 실행 `bin\dnp3-master-serial-mapping-first`는 정상 4순회와 앞의 두 장애 복구를 통과했지만,
부분 프레임 주입용 COM6 Open에서 AccessDenied로 중단됐습니다.
정확한 원인은 확정하지 않았으며, 대기·사전 Open을 추가한 최종 실행에서 전체 절차를 통과했습니다.
실패한 첫 실행도 원래 Modbus 앱 복원과 COM6 읽기를 확인했습니다.

### 최종 실물 결과

`bin\dnp3-master-serial-mapping-handover`의 `status.json`은
`Status=completed`, `Transport=Serial`, `Restored=true`, `Error=null`입니다.
같은 모델·바인딩·Master로 정상 **5순회**, 장애 **3회**, 피어 재시작 **3회**를 완료했습니다.
총 **44,465개 검증**, **7,112개 측정값**, **6,720개 이벤트**,
그중 **2,240개 Unsolicited**를 확인했습니다.
전체 측정값에는 두 Timeout 장애 준비 Static 조회의 각 56개도 포함합니다.
부분 프레임 만료 직후 모델 수신 누계는 변하지 않았고, 이후 최종 정상 순회도 통과했습니다.

두 번째 정상 순회 이후 GC 후 가용 힙의 기준·최소·최대는 모두 **727,572 B**로,
정상 순회 완료 시점의 잔류 증가는 0 B였습니다.
이는 peak/native/UART 메모리나 전체 통신 무할당을 뜻하지 않습니다.
원래 Modbus 앱은 첫 복원 시도와 COM6 holding 0..2의 123/456/65535 읽기를 통과했습니다.
단계별 값은 `measurements.jsonl`, 원본 피어 로그는 `peer-*.log`,
복원 증거는 `restored-modbus.log`에 보존합니다.
이 결과는 공유 실물 검증기 실행이며, UART 전용 샘플의 장시간 자동 재접속 시험은 아닙니다.

### 양 역할 Serial CRC 손상·복구

```powershell
.\Build\Test-Dnp3MasterMappingSoak.ps1 -AllowDeviceChanges -SerialValidation -SerialCrc `
  -OutputPath .\bin\dnp3-serial-crc-new
```

`-SerialCrc`는 고정 Serial 모드 전용이며 `-SerialSoak`와 함께 사용할 수 없습니다.
정상 2순회 후 독립 피어를 종료하고 PC가 COM6를 열어 MCU Master와 Outstation 각각에
헤더 CRC 4회·데이터 CRC 4회·교대 혼합 6회 손상 뒤 정상 프레임을 연속 전송합니다.
정확한 CRC 오류·폐기 바이트 수, 손상 프레임이 Endpoint에 전달되지 않는지 여부, 정상 측정값 1회 또는
정상 Direct Operate 1회와 Class 0 Read 응답을 확인합니다. 물리 출력을 구동하지 않습니다.
각 검사는 새 Endpoint와 세션을 사용하며 마지막에 원본 OpenDNP3 피어를 재시작해 정상 수신합니다.

공유 `SerialCrcValidation`은 저장소 코덱으로 입력을 생성하고 응답을 검사하는 시험 전용 도구입니다.
CRC 시험 자체는 독립 구현 상호운용 시험이 아니며, PC에서는 7바이트 단위의 수신 분할과
입력 누락 시 실패도 검사합니다. UDP 진단 응답은 측정값 18개를 포함한 88바이트이므로
`MasterSoak.cs`와 실행기를 함께 갱신해야 합니다.

2026-10-27 `bin\dnp3-serial-crc-20261027-2210`에서 양 역할 6종·CRC 오류 총 28회를 확인했습니다.
정상 3순회/4,200개 측정값과 마지막 독립 피어 복구를 통과했고,
준비 후 GC 가용 힙은 기준·최소·최대 모두 726,660 B였습니다.
`status.json`의 `SerialCrcCases=6`, `SerialCrcErrors=28`, `Restored=true`, `Error=null`과
`measurements.jsonl`, `crc-*.bin`, `restored-modbus.log`에 결과를 보존합니다.
각 기록의 `CrcErrors`/`DiscardedBytes`는 마지막 CRC 검사 값이며 누계가 아닙니다.
원래 Modbus 복원과 COM6의 123/456/65535 읽기도 성공했습니다.

### Serial 장시간 반복 수신

`-SerialValidation -SerialSoak`를 함께 지정하면 같은 UART·모델·바인딩·Master를 유지하는
시간 기반 시험을 실행합니다. 고정 시험과 같은 8-round 수신 검사를 반복하며,
기본 10순회마다 ACK Timeout → Application 응답 Timeout → 부분 프레임 만료를 번갈아 주입합니다.
각 장애에서 피어를 다시 시작하고 다음 정상 순회에서 수신을 재확인합니다.
MCU 포트 자체의 재개방이나 물리 배선 단절·전원 차단을 시험하는 것은 아닙니다.

```powershell
.\Build\Test-Dnp3MasterMappingSoak.ps1 -AllowDeviceChanges -SerialValidation -SerialSoak `
  -DurationMinutes 120 -OutputPath .\bin\dnp3-master-serial-soak-2h-new
```

`-SerialSoak`만 지정하는 잘못된 조합은 장치 변경 전에 거절합니다.
`DurationMinutes` 기본값은 기존 TCP와 같은 480분이므로, 2시간 실행에는 위처럼 120분을 명시합니다.
짧은 예행에는 `-DurationMinutes 5 -FaultEveryCycles 1 -CyclePauseSeconds 0`을 사용할 수 있습니다.
단, 지정 시간이 끝날 때 세 장애를 모두 경험하지 못했으면 실패하므로,
보드 처리 속도와 장애 간격에 맞게 충분한 시간을 확보해야 합니다.
지정 시간이 끝난 뒤에도 마지막 정상 복구 순회와 원래 앱 복원을 수행하므로
프로세스 종료 시각은 요청한 수신 시험 시간보다 늦습니다.

`status.json`의 `Transport=Serial`, `Mode=Timed`와 시작·예정 종료 UTC를 확인하십시오.
`SerialAckTimeouts`, `SerialResponseTimeouts`, `SerialPartialExpiries`는 각각 성공적으로 감지한 장애 누계이며,
`PeerRestarts`는 그 뒤 피어 재시작 누계입니다.
정상 순회마다 1,400개 측정값·1,344개 이벤트·448개 비요청 이벤트의 증가를 검사합니다.
각 Timeout 준비 조회는 측정값 56개를 추가하지만, 부분 프레임은 측정값을 추가하지 않아야 합니다.
두 번째 정상 순회 이후 정상 완료 시점의 GC 후 가용 힙이 기준보다 줄면 실패합니다.
아래 TCP 시험과 같이 `stop.request`로 정상 중단할 수 있으며,
`Status=completed`, `Restored=true`, `Error=null`을 확인하기 전에는 최종 성공이 아닙니다.
장시간 성공 결과는 해당 실행의 상태·단계 로그와 복원 증거를 확인한 뒤 기록합니다.

`bin\dnp3-master-serial-soak-rehearsal`의 5분 설정 예행은 마지막 복구를 포함해
정상 6순회·53,538개 검증·8,568개 측정값·8,064개 이벤트·2,688개 비요청 이벤트를 확인했습니다.
ACK Timeout 2회·Application 응답 Timeout 1회·부분 프레임 만료 1회와 피어 재시작 4회를 통과했고,
준비 후 정상 GC 가용 힙은 727,572 B로 일정했습니다.
첫 복원 배포와 COM6 읽기까지 통과했습니다. 이는 단기 예행이며 2시간 본시험 성공을 뜻하지 않습니다.

### Serial 2시간 본시험 결과

`bin\dnp3-master-serial-soak-2h-20261026`은 한국 시각 2026-10-26 07:33:26에 시작해
09:34:54에 마지막 정상 복구 순회를 마쳤고, 09:35:35에 원래 앱 복원까지 완료했습니다.
요청한 120분을 넘겨 수신을 수행했으며, `status.json`은
`Status=completed`, `Transport=Serial`, `Mode=Timed`, `Restored=true`, `Error=null`입니다.

| 항목 | 결과 |
|---|---:|
| 마지막 복구를 포함한 정상 순회 | 115회 |
| 검증 | 1,011,106개 |
| 전체 수신 측정값 | 161,448개 |
| 이벤트 | 154,560개 |
| 그중 Unsolicited 이벤트 | 51,520개 |
| Link ACK Timeout / Application 응답 Timeout / 부분 프레임 만료 | 4회 / 4회 / 3회 |
| 피어 재시작 | 11회 |
| 준비 후 정상 GC 가용 힙 기준/최소/최대 | 모두 727,572 B |
| 정상 순회 GC 잔류 증가 | 0 B |

전체 수신은 정상 115순회의 각 1,400개와 8회 Timeout 준비 조회의 각 56개 합계입니다.
세 번의 부분 프레임 만료에서는 FrameTimeouts=1을 확인하고 수신 측정값·이벤트 누계는 증가하지 않았습니다.
ACK 장애에서는 재전송 2회, Application 응답 장애에서는 Link 재전송 0회를 각각 확인했습니다.
각 장애 뒤 정상 수신을 재개했고, 마지막 정상 순회도 통과했습니다.
127개 단계별 기록 전체의 순회·장애·수신 증가량을 대조했으며 정상 완료 시점의 가용 힙은 모두 같았습니다.
원래 Modbus 앱은 첫 복원 시도와 COM6 holding 0..2의 123/456/65535 읽기를 통과했습니다.
근거는 `measurements.jsonl`, `peer-*.log`, `restored-modbus.log`에 보존합니다.
이는 동일 MCU 포트를 유지한 공유 검증기에서 세션을 반복하고 드레인 후 장애를 주입한 시험이며,
UART 전용 운영 샘플의 포트 재개방, 미확인 이벤트 영속 보존 또는 전원 차단 내구성을 증명하지 않습니다.

## TCP 손상 프레임·부분 프레임 실물 복구

```powershell
.\Build\Test-Dnp3MasterMappingSoak.ps1 -AllowDeviceChanges -TcpRecovery `
  -OutputPath .\bin\dnp3-tcp-recovery-new
```

Serial 옵션과 함께 사용할 수 없는 고정 절차입니다. 정상 독립 OpenDNP3 수신 1순회 후
피어를 종료하고 MCU 시험 전용 TCP 20003으로 PC가 연결합니다. 방화벽을 변경하지 않습니다.
같은 수신 소켓을 유지하되 각 검사마다 새 연결 소켓·Endpoint·세션을 사용하며,
MCU Master/Outstation 각각 헤더 CRC 4개·데이터 CRC 4개·교대 CRC 6개 뒤 정상 프레임을 연속 전송합니다.
부분 프레임은 `05 64 14` 뒤 1.2초 기다리고 같은 연결에서 정상 프레임을 전송합니다.
CRC 오류·폐기 바이트 수와 부분 프레임 만료 1회, 손상 프레임 미전달, 정상 Counter=123 1회 또는
Direct Operate 1회와 Class 0 Read 응답을 요구합니다. 이 거절은 세션 Faulted가 아니어야 합니다.
연결 종료·endpoint 예외의 Faulted 정책과 구분하며 제어는 메모리 모델만 사용합니다.

입력/응답 검사는 Serial 시험과 같은 저장소 코덱 fixture를 재사용하며 독립 CRC 상호운용은 아닙니다.
TCP 시험용 이미지에는 공유 시험기 때문에 Serial 참조도 포함하지만 UART는 열지 않습니다.
PC 루프백에서도 양 역할 8종과 7바이트 단위의 송신 분할을 확인합니다.
마지막에는 원본 OpenDNP3 피어를 재시작해 정상 매핑 1순회를 다시 완료합니다.

`bin\dnp3-tcp-recovery-20261028-0005`에서 8종, CRC 오류 28회와 부분 만료 2회를 확인했습니다.
앞뒤 정상 2순회는 총 2,912개 측정값/2,688개 이벤트/896개 Unsolicited입니다.
예열·각 장애·마지막 순회의 GC 후 가용 힙은 모두 735,996 B였으며 장기 누수 부재 주장은 아닙니다.
`status.json`의 `TcpRecoveryCases=8`, `TcpCrcErrors=28`, `TcpPartialExpiries=2`,
`Restored=true`, `Error=null`과 `tcp-recovery-*.bin`/`measurements.jsonl`/복원 로그를 확인하십시오.
개별 `CrcErrors`/`FrameTimeouts`/`DiscardedBytes`는 마지막 검사 값이고 전체 수명 동안의 누계가 아닙니다.
기존 `Checks`는 매핑 검사 수이며 새 시험기 내부의 검증 수를 더하지 않습니다.

최초 `bin\dnp3-tcp-recovery-20261027-2355`는 PC 수신형 연결 대기에서 실패했으며 원인은 확정하지 않았습니다.
다음 `bin\dnp3-tcp-recovery-20261028-0000`은 MCU 수신 소켓을 검사마다 재생성하다 두 번째 단계에서
SocketException 10035로 실패했습니다. 최종 구현은 하나의 수신 소켓을 수명 내내 유지하고
수락한 연결 소켓만 교체합니다. 세 실행 모두 원래 Modbus 복원과 COM6 읽기를 통과했습니다.

## 최신 단기 견고성 재검증 (2026-10-29)

다음 기록은 이 문서의 과거 장시간 시험과 별도의 최신 이미지 재검증입니다.
정상 매핑 통신의 외부 피어는 원본 OpenDNP3이며, CRC/부분 프레임 주입은 자체 시험 코덱입니다.

| 증거 (`bin` 아래) | 확인 범위 |
|---|---|
| `dnp3-master-soak-20261029-115537` | TCP 정상 8순회·73,854개 검사, 모델을 유지한 채 피어 재시작 3회·연결 강제 단절 3회와 최종 정상 수신 |
| `dnp3-master-soak-20261029-120122` | 양 역할 TCP header/data/mixed CRC와 부분 프레임 만료 8종, CRC 오류 28회·부분 만료 2회 및 정상 교환 복구 |
| `dnp3-master-soak-20261029-120508` | 양 역할 Serial header/data/mixed CRC 6종과 원본 피어 정상 수신 복구 |
| `dnp3-master-soak-20261029-121035` | Serial 44,465개 검사, ACK 재시도 2회 소진·Application 응답 만료·부분 프레임 만료 뒤 정상 교환 |

Serial 정상 복구 순회의 GC 후 보존 힙 증가는 0 B였으며 원래 Modbus 복원·COM6 읽기도 통과했습니다.
이는 단기 기능·복구 재검증이지 최신 코드의 8시간/2시간 시험 재수행이나
네이티브 RTS/전원 차단/모든 현장 조합의 검증을 뜻하지 않습니다.

## 장시간 안정성과 통신 장애 복구 시험

`Build\Test-Dnp3MasterMappingSoak.ps1`는 같은 MCU 모델·바인딩·Master를 유지한 채
조회·이벤트·Unsolicited 검증을 반복합니다. 한 순회는 위의 8-round 검증과 두 TCP 연결입니다.
8시간 동안 단일 TCP 연결을 유지하는 시험이 아니라, 지속적인 모델 수명과 반복 연결의 안정성 시험입니다.
각 순회는 1,456개 측정값/1,344개 이벤트/448개 비요청 이벤트를 정확히 검사합니다.
7종 Point 값·품질·시각, 같은 index 이벤트 순서, Confirmation 후 드레인도 계속 확인합니다.

```powershell
.\Build\Build-Dnp3ValidationMaster.ps1
.\Build\Test-Dnp3MasterMappingSoak.ps1 -AllowDeviceChanges `
  -DurationMinutes 480 -OutputPath .\bin\dnp3-master-soak-8h-new
```

PC 회귀·장애 프록시 시험과 MCU 이미지 빌드가 통과한 뒤에만 배포합니다.
원본 OpenDNP3 라이브러리는 변경하지 않으며, 시험 드라이버의 별도 `soak` 모드만 사용합니다.
기존 `physical` 모드의 generation 32~35 경계값 시험을 그대로 유지하면서,
`soak` 모드에서는 그 구간도 일반적인 단조 generation 값으로 검증합니다.

기본 10순회마다 두 장애를 번갈아 주입합니다.
먼저 MCU가 정상 연결에서 Static을 확인하고, UDP로 장애 주입 준비를 알립니다.

- **피어 종료·재시작**: PC가 독립 Outstation을 정상 종료합니다.
  MCU는 다음 명시적 조회의 통신 예외와 `session.Faulted`를 확인하고 소켓을 폐기합니다.
  PC는 새 프로세스를 시작하고 시험 측 원격 generation을 0으로 재동기화합니다.
  앱 모델·Master·누적 수신 계수는 초기화하지 않습니다.
- **기존 TCP 연결 강제 종료**: PC가 같은 피어에 별도 TCP 연결을 열어
  원본 서버의 `ServerAcceptMode.CloseExisting`로 MCU 연결을 퇴출시킵니다.
  MCU가 실제 통신 예외와 Faulted를 확인한 뒤 주입용 연결을 닫습니다.
  원격 프로세스·Database·generation은 유지되며 다음 순회에서 새 세션으로 값을 재확인합니다.
  이 경로는 의도적인 연결 종료이지 TCP RST 패킷이라는 주장은 아닙니다.
  PC 회귀에서는 별도 데스크톱 프록시의 `Linger(0)` RST도 검증합니다.

예상 오류는 IO/Socket/Timeout 종류와 누계를 기록하며,
예외 없이 계속 동작하거나 Faulted가 설정되지 않으면 실패합니다.
정상 순회의 통신 예외, 값 불일치 또는 프로토콜 오류는 예상 장애로 무시하지 않습니다.
장애는 앞선 이벤트의 Confirmation·드레인이 끝난 뒤 주입하므로
미확인 이벤트의 장애 중 보존/영속 exactly-once를 증명하지 않습니다.
지정 시간이 끝나도 두 장애를 모두 경험하지 못했으면 실패하며, 마지막 장애 뒤에는
한 번 더 정상 순회를 완료해야 성공합니다.

UDP 20002의 순번 기반 제어는 같은 요청이 재전송돼도 실행을 반복하지 않습니다.
MCU 재부팅/응답 소실/순번 불일치가 복구되지 않으면 명령 제한 시간 180초 후 실패합니다.
두 번째 정상 순회 후 GC 가용 힙을 기준으로 잡고, 이후 **정상 순회 완료 시점**에
그보다 가용 힙이 줄면 GC 후 잔류 관리 메모리가 증가한 것으로 보아 실패합니다.
장애 직후에는 마지막 수신 배열 등의 수명이 다르므로 별도 단계의 힙 측정값으로 기록합니다.
`MinimumHeap`/`MaximumHeap`은 준비 후 정상 순회 표본의 범위이며 최대 사용량·네이티브·Wi-Fi RAM을 나타내지는 않습니다.

출력 폴더의 `status.json`은 PID·시작/예정 종료 UTC·순회/장애 누계·최근 측정·복원 여부를 저장합니다.
`measurements.jsonl`은 각 정상/장애 단계의 검증·수신·GC 계측을 보존합니다.
`Status=completed`와 `Restored=true`를 모두 확인해야 최종 성공입니다.
실패/종료 시 기존 finally 복원과 COM6 읽기를 수행하며 복원 실패도 상태에 남깁니다.

정상 중단하려면 실행 폴더에 `stop.request` 파일을 만드십시오.
현재 명령과 순회가 끝난 뒤 제한 시간 내 중단하고 원래 앱을 복원합니다.
조기 중단은 성공으로 표시하지 않습니다. 프로세스 강제 종료/PC 종료는 finally 실행을 보장하지 않으므로
피하고, 불가피했다면 `Build\Restore-ModbusHardware.ps1 -LogPath <로그 경로>`로 직접 복원해야 합니다.
8시간 동안 PC 절전·재부팅과 COM3/COM6의 다른 용도 사용을 피하십시오.

### 단기 예행 결과

`bin\dnp3-master-soak-direct-rehearsal`의 3분 설정 예행은
마지막 복구 순회를 포함해 7순회·64,548개 검증·10,472개 측정값·9,408개 이벤트·3,136개 비요청 이벤트,
피어 재시작 3회와 TCP 연결 강제 종료 2회를 통과했습니다.
모든 정상 순회 완료 시점의 가용 힙은 741,444 B로 같았으며 장애 직후에는 736,308 B였습니다.
초기 예행의 min/max 집계에는 장애 단계도 포함돼 있었으므로,
장시간 실행은 이를 정상 순회 표본으로 분리했습니다.
첫 복원 후 COM6 응답이 없어 명시된 재시도 절차로 다시 배포한 뒤 읽기를 확인했습니다.

첫 중간 프록시 기반 MCU 예행(`bin\dnp3-master-soak-rehearsal`)은 TCP 접속 예외로 실패했습니다.
PC의 동일 프록시 시험은 통과했지만 MCU 경로의 원인은 확정하지 않았으며
방화벽 설정을 변경하거나 이를 성공 처리하지 않았습니다.
최종 하드웨어 경로는 기존 원본 피어에 직접 연결하며, 위 연결 교체 방식으로 장애를 주입합니다.
단기 예행 성공만으로 8시간 시험 성공을 주장하지 않습니다. 장시간 결과는 해당 실행의 상태·로그로 확인합니다.

최종 코드의 `bin\dnp3-master-soak-final-rehearsal` 2분 설정 재검증도
5순회·45,936개 검증·7,448개 측정값·6,720개 이벤트·2,240개 비요청 이벤트,
피어 재시작 2회/TCP 강제 종료 1회를 통과했습니다.
정상 순회의 가용 힙 기준·최소·최대는 모두 **741,432 B**였고, 첫 복원 시도와 COM6 읽기도 통과했습니다.

### 8시간 본시험 결과

`bin\dnp3-master-soak-8h-20261025`는 한국 시각 2026-10-25 22:42:59에 시작해
2026-10-26 06:43:27에 마지막 정상 복구 순회를 완료했습니다.
실제 수신 시험은 8시간을 넘겼으며, 06:44:09에 원래 앱 복원까지 마쳤습니다.
`status.json`은 `Status=completed`, `Restored=true`, `Error=null`입니다.

| 항목 | 결과 |
|---|---:|
| 마지막 복구를 포함한 정상 순회 | 887회 |
| 검증 | 8,017,119개 |
| 전체 수신 측정값 | 1,296,400개 |
| 이벤트 | 1,192,128개 |
| 그중 Unsolicited 이벤트 | 397,376개 |
| 피어 종료·재시작 | 44회 |
| 기존 TCP 연결 강제 종료 | 44회 |
| 준비 후 정상 순회 GC 가용 힙 기준/최소/최대 | 모두 741,432 B |
| 정상 순회 GC 잔류 증가 | 0 B |

전체 측정값에는 88회의 장애 준비 Static 조회(각 56개)도 포함합니다.
의도한 88개 통신 장애를 모두 명시적으로 감지했으며,
각 장애 후 새 세션에서 값·품질·시각과 이벤트 수신을 재확인했습니다.
정상 순회 오류나 값 불일치는 없었고, 마지막 복구 순회도 통과했습니다.
원래 Modbus 앱은 첫 복원 시도에 배포됐으며 COM6 holding 0..2의 123/456/65535 읽기를 확인했습니다.
976개 단계별 표본은 `measurements.jsonl`, 원본 피어 로그는 `peer-*.log`,
복원 증거는 `restored-modbus.log`에 보존합니다.
이 결과는 위 반복 연결·드레인 후 장애 조건의 검증이며,
8시간 단일 TCP 연결 유지나 미확인 이벤트의 장애 중 영속 보존을 증명하지 않습니다.
