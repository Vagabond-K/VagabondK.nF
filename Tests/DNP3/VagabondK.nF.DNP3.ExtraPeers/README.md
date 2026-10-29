# 추가 독립 DNP3 피어

OpenDNP3 원본에서 확인한 subnormal/음의 0 디코딩 한계를 별도 구현체로 교차 확인하는
**PC 루프백 TCP** 및 **실제 MCU↔Go RS485** 시험입니다.
TCP에서는 동일 VagabondK.nF 소스를 .NET PC 드라이버에서, RS485에서는 nanoFramework MCU에서 실행합니다.
운영용 라이브러리 소스와 독립 구현체 원본을 수정하지 않으며 전체 DNP3 적합성 시험은 아닙니다.

## 독립성 및 버전

| 피어 | 고정 버전 | 역할 | 라이선스 |
|---|---|---|---|
| [otfabric/go-dnp3](https://github.com/otfabric/go-dnp3) | v1.4.1, `d43c5e1835c556acc093e4bf044b3fadc934137a` | Master와 Outstation | MIT |
| [craigpnnl/dnp3py](https://github.com/craigpnnl/dnp3py) | v0.6.0, `c00d5df22e779df72f7caa4402c3f34957eb276d` | Master | MIT |

각 피어 자체의 링크 CRC·전송 계층·애플리케이션 계층·객체 코덱으로 통신합니다.
Python의 dnp3-python/pydnp3(OpenDNP3 래퍼)은 사용하지 않습니다.
Go는 `go.mod`/`go.sum`에 버전을 고정하고 모듈 캐시의 원본 체크섬을 확인합니다.
Python은 원본 체크아웃의 커밋이 지정한 값과 같고 추적 파일이 수정되지 않았는지 확인합니다.
프로젝트가 신생이며 이 시험 결과는 인증이나 제작사의 다른 기능 주장을 보증하지 않습니다.

별도 GPL peer를 로컬 시험 실행 파일로 사용하는 것도 그 사실만으로 VagabondK.nF에 GPL을
적용하지는 않습니다. 별개 프로그램끼리 표준 DNP3 TCP/시리얼로 통신하는 방식과 GPL 코드를
제품에 복사/링크하는 방식은 구분해야 합니다. 사적으로 실행·수정만 하는 경우와
GPL 실행 파일/수정본/결합물을 외부에 배포하는 경우의 의무도 다릅니다.
이는 일반적인 GPL 설명이며 구체적인 결합·배포 방식은 해당 라이선스를 따로 검토해야 합니다.
이번 시험은 MIT peer만 사용하며 GPL 코드를 제품/패키지에 포함하지 않습니다.

## PC TCP 검사 범위

g30v5/6, g40v3/4, g32v5/6/7/8, g42v5/6/7/8의 12종을 각 84포인트로 전송합니다.
명시적으로 지정한 float64 비트 패턴 20개와 재현 가능한 float32 비트 패턴 64개를 별도로 생성합니다.
양/음의 0, 양/음의 float32/64 최소 subnormal, 최대 subnormal·최소 normal,
float32 최대 유한값, 소수 및 반올림 경계를 포함합니다. NaN/Infinity는 범위 밖입니다.
float32 표현은 변환 후의 기대값을, float64는 원값의 **64비트 전체**를 비교합니다.
서브노멀을 float32로 변환하면서 0이 되는 경우는 예상된 변환 결과로 구분하며 float64 원값 보존과 혼동하지 않습니다.

각 언어에서 기대값을 따로 만들고 피어의 실제 측정 콜백 결과를 비교합니다.
문자열 소수/`==`만으로 비교하지 않으므로 음의 0 손실도 실패합니다.
인덱스·개수·중복·품질과 시각도 확인합니다. 256바이트 응답 Fragment를 사용하여
여러 Application Fragment와 확인 응답을 주고받습니다. 최초 이벤트가 확실히 생성되도록
품질을 0에서 0이 아닌 값으로 바꾸며, 시각만 바뀌는 갱신이나 음의 0만으로 바뀔 때의 이벤트 감지는 시험하지 않습니다.

2026-10-24 실행 결과:

| 경로 | 수치/품질 | 시각 |
|---|---|---|
| Go Master ← VagabondK.nF Outstation | 1,008개 정확 일치 | 1,008개 정확 일치, 0~48비트 최대 포함 |
| Python Master ← VagabondK.nF Outstation | 1,008개 정확 일치 | 928개 일치, 80개 날짜 표현 한계 |
| VagabondK.nF Master ← Go Outstation | 1,008개 정확 일치 | 1,008개 정확 일치, 1~48비트 최대 포함 |

**수치/품질 3,024개를 통과했으며 OpenDNP3에서 실패했던 subnormal/음의 0은 이 피어들에서 보존됩니다.**
기존 OpenDNP3 실패를 소급해서 통과 처리하지는 않습니다.

Python master 콜백은 `datetime`을 사용하므로 연도 9999를 넘는 48비트 최대 부근 시각을
`None`으로 반환합니다. 최대/최대-1ms 80건은 시간 보존 성공으로 집계하지 않습니다.
숫자형 timestamp 객체 코덱과 master 측정 콜백의 표현 범위를 혼동하지 않습니다.
이는 상대 구현체의 한계로 남기며 원본을 수정하거나 기대값을 정상 시각으로 치환하지 않습니다.
Go Outstation DB의 공개 API는 시각 0을 자동 현재시각 지정으로 정의하므로 이 경로의
전송 벡터는 명시적으로 1ms를 사용합니다. 시각 값 0 자체의 전송 성공으로 집계하지 않습니다.

## PC TCP 재실행

Go 1.23 이상과 Python 3.11 이상 실행 파일을 준비합니다. 이번 환경은 작업 폴더 내부에
Go 1.27.3 공식 SHA256 확인본과 Python 3.13.7 embeddable(PSF 서명 확인본)을 사용했습니다.
시스템 PATH/설정을 변경하지 않으며 다운로드한 도구/원본은 `bin\protocol-tools\extra-peers`에만 있습니다.

```powershell
# 아직 없을 때만 독립 Python 원본 준비:
git clone https://github.com/craigpnnl/dnp3py.git .\bin\protocol-tools\extra-peers\dnp3py
git -C .\bin\protocol-tools\extra-peers\dnp3py checkout c00d5df22e779df72f7caa4402c3f34957eb276d
# 기본은 현재 작업 폴더의 portable 실행 파일 사용. 외부 설치본은 절대 경로 인자로 지정 가능:
.\Build\Test-Dnp3ExtraPeers.ps1 -ReportKnownLimits
# 엄격 모드: 현재 Python 시각 한계 80건을 보고한 뒤 명시적으로 실패:
.\Build\Test-Dnp3ExtraPeers.ps1
```

`-GoPath`, `-PythonPath`, `-Dnp3PyPath`로 설치 경로를 지정할 수 있습니다.
`-ReportKnownLimits`는 원본 한계를 숨기지 않는 진단 보고 모드이며 무조건 전체 PASS가 아닙니다.
원시 로그는 `bin\protocol-tools\extra-peers\numeric-*.log`입니다.
시험용 서버는 모두 `127.0.0.1`의 OS 할당 포트를 사용하고 종료합니다.
방화벽을 변경하지 않으며 보드/COM3/COM6를 사용하지 않아 기존 Modbus 앱은 그대로 유지됩니다.
PC 피어의 연결 종료·초기화는 프로세스 종료 코드 0과 전체 비교 완료 마커를 확인한 경우에만
정상 종료로 인정합니다. 종료 예외만 무시하여 통신 실패를 통과시키지 않습니다.

## 실제 RS485 검사

동일 원본 Go v1.4.1과 Geekble Nano ESP32-S3의 nanoCLR 1.18.0.30을 직접 연결했습니다.
Go의 `client.WithStream`/`server.ServeConn`에 `go.bug.st/serial` v1.6.4(BSD-3-Clause)의
COM6 스트림을 전달합니다. TCP 브리지, .NET DNP3 디코더나 Python UART 피어는 사용하지 않습니다.
배선은 기존 [시리얼 Host](../../../Samples/DNP3/VagabondK.nF.DNP3.SerialHost/README.md#실제-rs485-검증) 그대로입니다.
MCU COM2(GPIO17/18)↔DM2472 자동 방향 RS485↔CS-428↔PC COM6,
9600 8E1, PC RTS/DTR=true, MCU Normal UART이며 COM3은 배포/디버그 전용입니다.

2026-10-24 최종 새 세션 결과:

| 실제 경로 | 정확 비교 | MCU 링크 재시도 / CRC / 프레임 기한 오류 |
|---|---|---|
| Go Master ← MCU Outstation | 12종 × 84 = 1,008개 수치 비트·품질·시각 | 0 / 0 / 0 |
| MCU Master ← Go Outstation | 12종 × 84 = 1,008개 수치 비트·품질·시각 | 0 / 0 / 0 |

합계 **2,016개**이며 양/음의 0, float32/64 subnormal·normal 경계와 48비트 시간 경계를
검사했습니다. float32 변환의 기대값과 Go Outstation의 시각 0→명시적 1ms 계약은 PC 시험과
동일합니다. Go Master 수신 방향은 시각 값 0 자체도 확인했습니다. 이는 아날로그 두 타입의 12종
표현 시험이며 전체 45종, 비요청 기능, 실제 RTC 드리프트를 새 Go 실물 시험으로 검증한 것은 아닙니다.
각 회차 168개 포인트의 품질을 변경해 이벤트를 생성하고 요청 외 잔여 이벤트도 배출합니다.
양쪽 confirmed link와 256바이트 application fragment를 사용합니다.
CROB 12회는 데이터 준비, 마지막 1회는 완료 표시 전용이며 물리 출력을 구동하지 않습니다.

최초 Go Outstation 시험은 기본 설정에서 비요청 기능이 비활성화되어 있어
`DISABLE_UNSOLICITED` 요청도 `NO_FUNC_CODE_SUPPORT`로 거절했습니다.
수치 전용 MCU 시험에서 불필요한 비활성화 요청을 제거했으며 오류 응답을 성공 처리하거나
원본 Go 라이브러리를 수정하지 않았습니다. 실패 콘솔은
`go-numeric-serial-outstation-mcu.unsolicited-disabled-failure.log`로 보존했습니다.
최초 Go Master 시험에는 MCU 재시도 3회가 있었습니다. Go 수신 루프를 READY 전에 시작한
최종 재시험에서는 0회였으며 처음의 3회를 오류 없는 실행으로 소급 집계하지 않습니다.

### RS485 재실행

현재 COM3/COM6의 격리 시험 장치와 원본 복원 이미지 전용입니다.
아래 빌드는 순차 실행해야 하며 nanoFramework 프로젝트를 병렬 Rebuild하지 마십시오.
Go 실행 파일은 앞의 PC 시험 스크립트로 준비하거나 다음과 같이 빌드합니다.

```powershell
$env:GOTOOLCHAIN = 'local'
$env:GOPATH = Join-Path $PWD 'bin\protocol-tools\extra-peers\gopath'
$env:GOCACHE = Join-Path $PWD 'bin\protocol-tools\extra-peers\gocache'
& .\bin\protocol-tools\extra-peers\go\bin\go.exe -C .\Tests\DNP3\VagabondK.nF.DNP3.ExtraPeers\go-peer build -mod=readonly -o ..\..\..\..\bin\protocol-tools\extra-peers\go-peer.exe .
if ($LASTEXITCODE -ne 0) { throw 'Go build failed.' }
foreach ($role in @('Outstation', 'Master')) {
    MSBuild .\Samples\DNP3\VagabondK.nF.DNP3.SerialHost\VagabondK.nF.DNP3.SerialHost.nfproj /t:Rebuild /p:Configuration=Release /p:Dnp3HardwareTest=true /p:Dnp3NumericTest=true "/p:Dnp3Role=$role"
    if ($LASTEXITCODE -ne 0) { throw "MCU $role build failed." }
    Copy-Item .\Samples\DNP3\VagabondK.nF.DNP3.SerialHost\bin\Release\VagabondK.nF.DNP3.SerialHost.bin ".\bin\dnp3-hardware\Dnp3.Numeric$role.bin"
}
.\Build\Test-Dnp3GoSerial.ps1
```

래퍼는 원본 `restore-Modbus.SerialServer.bin`의 SHA256을 확인한 후 양방향을 실행하며,
성공/실패와 관계없이 `finally`에서 원래 Modbus 앱을 복원합니다. 8초 시작 대기 후
COM6에서 holding `123,456,65535` 읽기도 요구합니다. 복원 이미지를 새 시험 이미지로 덮어쓰지 마십시오.
최종 실행에서도 복원과 실제 읽기를 완료했습니다.

증거는 `bin\dnp3-hardware\go-numeric-serial-master.log`, `-master-mcu.log`,
`go-numeric-serial-outstation.log`, `-outstation-mcu.log`,
`go-numeric-serial-run.log`, `restored-modbus-go-numeric.log`입니다.
시험 실행기는 Go 프로세스 성공·완료 마커와 MCU의 1,008개/12회 완료 및 CRC/프레임 기한 오류 0을
함께 요구합니다. 시리얼 드라이버는 시험 도구 의존성일 뿐 nanoFramework DLL/NuGet 의존성이 아닙니다.
