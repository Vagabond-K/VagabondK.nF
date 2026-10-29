# 독립 BACnet/IP MCU 호스트

`VagabondK.nF.BACnet`(객체/서비스/BVLC/NPDU/APDU)와 `VagabondK.nF.BACnet.IP`(UDP 어댑터)는
Modbus·DNP3에 의존하지 않습니다.
이 호스트는 독립 저장소로 AV0=250, 쓰기 가능한 AV100=800과 BV0=1을 제공합니다.
저장소는 RAM의 시험 값만 즉시 갱신하며 물리 I/O를 수행하지 않습니다.

기본 빌드는 소켓을 열지 않습니다. 격리된 실물 시험을 실행하려면 `BacnetHardwareTest=true`를 명시해야 합니다.
기존 Wi-Fi AutoConnect 설정을 사용하며 UDP 포트는 47808, Device는 12345, Vendor는 0입니다.
Vendor 0은 평가용 임시 값이지 정식으로 할당받은 ID가 아닙니다.

## 지원 범위와 저장소 계약

최대 64개 AV/BV, Who-Is/I-Am, ReadProperty, non-commandable WriteProperty,
Object_List/array index, 서비스·객체 bitstring, Status_Flags/Reliability를 제공합니다.
BBMD/Foreign Device, routed NPDU, MS/TP, COV, priority array/NULL relinquish,
ReadPropertyMultiple, segmentation은 지원하지 않습니다. 지원하지 않는 서비스/속성/형식은
Reject/Abort/Error로 응답합니다. 전체 BACnet 규격/BTL 인증을 의미하지 않습니다.

`IBacnetValueStore`의 객체 개수/정의는 생성 이후 불변입니다.
`Read`는 값과 품질을 원자적으로 읽은 스냅샷을 반환하고, `QueueWrite`는 해당 호스트의 쓰기 큐 정책을 구현합니다.
AV는 정확한 IEEE754 float32 비트, BV는 0/1이며 WriteProperty는 비유한 AV를 거부합니다.
`Good=false`는 fault 상태와 reliability=unreliable-other를 노출합니다.

`BacnetDevice.Process`와 `BacnetIpSession.ProcessNext`는 한 스레드에서 직렬 호출합니다.
소켓 생성·바인딩·시간 제한·수명 관리·해제는 호출자 책임이며 세션은 소켓을 닫지 않습니다.
프로토콜 오류와 소켓·I/O 오류는 명시적 예외로 알립니다. 범위에 해당하지 않는 Who-Is에 응답하지 않는 것은 정상입니다.
UDP 데이터그램은 최대 512바이트이며 크기를 초과하거나 형식이 잘못된 입력은 거부합니다.

## 자동 재실행

수동 배선/전원 조작 없이 기존 COM3 MCU에 배포하고 독립 `BACnet` NuGet 4.0.0
클라이언트가 LAN에서 직접 통신합니다. COM6 데이터선은 사용하지 않습니다.

```powershell
.\Build\Test-BacnetHardware.ps1 -AllowDeviceChanges -Iterations 256
# 보드 없는 독립 UDP 회귀:
dotnet run --project .\Tests\BACnet\VagabondK.nF.BACnet.Interop -c Release -- --stress 1024
# 독립 2종 패키지 / 타 프로토콜 비의존 소비 이미지:
.\Build\Pack.ps1 -LibrarySet BACnet
.\Build\Test-Packages.ps1 -LibrarySet BACnet
```

하드웨어 실행기는 `Build\Restore-ModbusHardware.ps1`로 기존 Modbus 복원 이미지의 체크섬을 확인하며 성공/실패 모두 `finally`에서
원래 앱을 복원하고 COM6 holding `123,456,65535` 읽기를 요구합니다.
배포 전에도 SHA256을 검사하며, 빌드 단계 실패처럼 배포를 시도하지 않았으면 보드를 변경하지 않습니다.
명시적 IP 기본값은 현재 격리 평가 보드 `192.168.0.187`입니다.
방화벽을 자동 변경하지 않으며 다른 환경에서는 네트워크 허용 범위를 별도로 확인하십시오.
독립 클라이언트는 명시한 장치 IP의 OS 라우트로 로컬 IPv4를 선택합니다.
가상 NIC가 함께 있어도 브로드캐스트 인터페이스를 임의로 추측하지 않습니다.
PC 회귀는 64객체/65개 Object_List, 세그먼트 미지원 Abort, 정확한 오류 코드와
품질 변화를 검사하고, MCU 시험은 독립 저장소 경로를 검사합니다.

## 실물 결과

2026-10-24 `bin\bacnet-hardware-20261024-191924`에서 독립 BACnet 4.0.0과 직접 UDP로
유한 float32 비트의 경계값·난수와 BOOL 값에 대해 256회 쓰기·읽기 반복에 성공했습니다.
Who-Is/I-Am, Object_List, 품질, readonly/NaN/Infinity/잘못된 형식/priority/범위 거부와
0/4/513/1472바이트 잘못된 데이터그램 뒤의 정상 조회 복구를 포함합니다.
최종 실행기 종료 전 원본 Modbus를 복원하고 COM6 `123,456,65535` 읽기도 성공했습니다.
증거는 `bin\bacnet-hardware-final.log` 및 해당 폴더의 `independent.log`/`restored-modbus.log`입니다.
최종 독립 패키지 피드(`bin\nuget\bacnet`)는 2종만 포함하며,
Modbus/DNP3 의존성 없는 오프라인 복원과 package-only MCU 소비 이미지 빌드도 확인했습니다.

2026-10-27 `bin\bacnet-hardware-20261027-234751`에서 **손상 WriteProperty 32종**을 추가 확인했습니다.
AV100 요청을 10~23바이트에서 자른 14종, BV0 요청을 10~20바이트에서 자른 11종,
객체·속성·여는 태그·값 태그·닫는 태그 변조, 끝에 바이트 추가, 미지원 분할 7종입니다.
절단 시 BVLC 길이도 맞춰 APDU 경계 검사를 실제로 통과하도록 구성합니다.
매번 장치에서 정확한 invoke ID·Reject/Error/Abort 및 사유 코드가 돌아와야 하며,
AV100=123과 BV0=0을 다시 읽어 보존을 확인한 뒤 정상 AV/BV Write/Read도 수행합니다.
손상 입력은 수동 생성한 raw APDU이고, 전후 정상 Write/Read는 독립 BACnet 4.0.0 클라이언트입니다.
이 시험에는 계수용 진단 객체를 추가하지 않았으므로 값 보존은 확인하지만 내부 QueueWrite 호출 횟수까지
관측했다고 주장하지 않습니다. 기존 256회 유한 float32/BOOL 왕복도 완료했고
원래 Modbus 복원·COM6 읽기를 통과했습니다. `independent.log`와 `restored-modbus.log`에 보존합니다.

최초 실행은 PC의 여러 IPv4 인터페이스로 인한 클라이언트 설정 오류로 통신 전에 실패했습니다.
다음 실행은 BACnet MCU 시험은 성공했지만 최초 복원 읽기에서 시간 제한을 초과했으며,
이들을 전체 성공으로 소급 집계하지 않습니다. 최종 실행은 준비 확인까지 모두 성공했습니다.
