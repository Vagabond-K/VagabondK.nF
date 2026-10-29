# 실제 MCU DNP3 정적 매핑 샘플

Geekble Nano ESP32-S3의 저장된 Wi-Fi AutoConnect 설정을 사용하는 TCP Outstation입니다.
TCP 20000, Outstation 주소 1, Master 주소 2이며 GPIO나 실제 설비는 제어하지 않습니다.
네트워크/펌웨어/자격 증명을 변경하지 않습니다. 격리된 평가 보드에서만 사용하십시오.

`DeviceData.cs`에 7종 Point를 각각 인덱스 0으로 매핑합니다.
Contact=true, State=2, Total=1000, Frozen=999, Temperature=250,
Enabled=true, Setpoint=12.5가 초기값입니다. 공통 Flags=1, UTC 시각=1700000000000이며
Temperature Deadband는 0.5입니다.

Group 12 인덱스 0은 Count=1인 Latch On/Off만 허용합니다.
펄스·Trip/Close는 `NotSupported`로 거부하고, latch에서 의미 없는 On/Off 시간은 사용하지 않습니다.
Group 41 인덱스 0은 -1000~1000 범위 Setpoint를 지원합니다.
출력은 모델 상태일 뿐 물리 장치의 동작 성공을 의미하지 않습니다.
상태의 private setter와 명령 처리는 같은 모델 내부에서 실행하며, 명령이 성공할 때마다 UTC 시험 시각을 1씩 증가시킵니다.
이 시각은 실제 RTC가 아닌 이벤트 순서 검증용 고정 기준입니다.

입력 5종은 고정 시험값이고 변경 가능한 값은 출력 Status 2종입니다.
앱은 통신 처리 전에 `Publish(BinaryOutputStatus, 0)`과 `Publish(AnalogOutputStatus, 0)`을
호출하여 변경 불가능한 입력의 반복 읽기·변환·갱신을 피합니다.
첫 게시가 실패해도 두 번째 게시를 수행하며 실패 시 이벤트 초과를 Debug 로그에 남깁니다.
반복 게시를 유지하므로 초과 후에도 같은 값의 이벤트를 재시도할 수 있습니다.
이벤트 규칙은 기존 Database를 사용합니다. Flags만 바뀌면 이벤트를 만들고,
시각만 바뀌면 Static 시각만 갱신합니다. 같은 Flags의 Analog 변화량은 마지막 이벤트 기준
(첫 이벤트 전에는 초기값) Deadband를 초과해야 하며, 정확히 같은 경계값은 이벤트를 만들지 않습니다.
실제 센서 갱신은 같은 `sync` 객체로 잠근 구간에서 수행해야 합니다.
센서나 공유 품질을 변경하는 앱으로 확장할 때는 해당 Point도 명시적으로 게시하거나
전체 `Publish()`를 사용해야 합니다. 선택하지 않은 Point의 시각도 자동 갱신되지 않습니다.
연결 종료 시 세션을 교체하고 Station 연결 상태만 초기화하며 Database와 이벤트는 보존합니다.

```powershell
# 배포 전 PC/전체 이미지 사전 빌드, 실물 검증, finally에서 원래 Modbus 앱 복원
.\Build\Test-Dnp3MappingHardware.ps1 -AllowDeviceChanges -Iterations 128 -Rounds 100
```

독립 원본 OpenDNP3 Master로 7종 초기값, 직접/선택 제어, 잘못된 제어 거부,
정확한 Status·이벤트 값/시각 및 Confirmation 후 제거를 검사합니다.
보드 주소 기본값은 `192.168.0.187`, 배포 포트는 COM3입니다.
복원 이미지 SHA256 및 COM6의 원래 Modbus Holding 0..2=123/456/65535 읽기까지 확인합니다.
자원 측정은 `Tests\DNP3\VagabondK.nF.DNP3.Mapping.Hardware\README.md`를 참조하십시오.

`FloatFormats.cs`는 별도 인덱스 1의 float32 Analog/AnalogOutputStatus 예제입니다.
시작 시 `FloatData.Demonstrate()`가 네트워크 연결 전에 별도 Database를 초기값으로 만들고 변경값 게시와 이벤트 수를 검사합니다.
이 Database는 위 TCP Station에 연결하지 않으며, 7종/인덱스 0 실물 상호운용 결과와 구분합니다.
