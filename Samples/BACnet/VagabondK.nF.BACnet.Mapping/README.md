# BACnet AV/BV 정적 매핑 MCU 샘플

실제 nanoFramework MCU에서 저장된 Wi-Fi로 연결해 BACnet/IP UDP47808 Device를 구동합니다.
PC 루프백이 주 샘플이 아니며, GPIO/액추에이터·PLC·다른 프로토콜은 사용하지 않습니다.
Device instance=12345, Vendor ID=0, 이름은 `BACnet mapping evaluation`입니다.
인증·암호화가 없는 평가용 LAN 프로파일이므로 **격리 LAN에서만 사용**하십시오.

| 객체 | 멤버 | 초기값 | 쓰기 |
|---|---|---|---|
| AV0 | Temperature | 250f | 거절 |
| AV100 | Setpoint | 800f | 허용, private setter |
| BV0 | Enabled | true | 허용, private setter |
| BV1 | QualityControl | true | 허용, TemperatureGood에 반영 |

AV0의 Quality는 같은 모델의 `TemperatureGood` bool입니다.
BV1을 false로 쓰면 AV0 Status_Flags의 Fault=true, Reliability=7로 보입니다.
BV1은 원격 품질 전환을 확인하기 위한 시험용 제어이며 실제 센서 품질 정책을 대신하지 않습니다.
앱에서 모델을 갱신할 때도 저장소에 전달한 동일한 잠금 객체를 사용해야 합니다.
각 Read는 잠금 안에서 값·품질을 한 번에 읽고, 쓰기는 setter에 즉시 반영합니다.
객체명은 기존 Device의 `point-종류-instance` 규칙을 유지합니다.
객체명·단위·새 속성, COV, commandable/priority array, MS/TP는 추가하지 않습니다.

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' `
  .\Samples\BACnet\VagabondK.nF.BACnet.Mapping\VagabondK.nF.BACnet.Mapping.nfproj `
  /t:Rebuild /p:Configuration=Release
dotnet run -c Release --project .\Tests\BACnet\VagabondK.nF.BACnet.Mapping.Tests
```

`bin\Release\VagabondK.nF.BACnet.Mapping.bin`은 펌웨어 어셈블리 버전과 저장 Wi-Fi를
확인한 보드에만 배포하십시오. Generator/Roslyn DLL은 MCU에 배포하지 않습니다.
PC 테스트는 C#7.3 생성 코드와 진단, 100,000개 float32 원시 비트의 왕복 보존,
공유 잠금·예외 전파·직접 호출 시 추가 할당이 없는지 여부 및 외부 BACnet 4.0.0 UDP 클라이언트와의 통신을 확인합니다.

이미 이 샘플이 배포된 평가 보드는 아래 명령으로 실제 ReadProperty/WriteProperty를 시험합니다.
이 명령은 쓰기 가능한 모델 값을 변경하며 앱을 자동으로 복원하지 않습니다.

```powershell
dotnet run -c Release --project .\Tests\BACnet\VagabondK.nF.BACnet.Mapping.Tests -- --hardware 192.168.0.187 128
```

이 저장소의 전용 Geekble Nano ESP32-S3(COM3, COM6 원래 Modbus 앱)는 안전 배포·복원과
8/32/64개 자원 측정까지 포함하는 `Build\Test-BacnetMappingHardware.ps1 -AllowDeviceChanges`를
사용합니다. 범용 보드에는 실행하지 마십시오.
상세 절차·측정 범위는 `Tests\BACnet\VagabondK.nF.BACnet.Mapping.Hardware\README.md`,
선언 계약·패키지는 `Source\BACnet\VagabondK.nF.BACnet.Mapping\README.md`를 참조하십시오.
