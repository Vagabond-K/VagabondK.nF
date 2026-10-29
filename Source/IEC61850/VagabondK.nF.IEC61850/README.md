# IEC 61850 공통 형식

Client와 Server가 공유하는 제한형 IEC 61850 의미 매핑입니다.
일반 MMS core만 참조하며 MMS/IEC Client·Server 및 Socket DLL은 참조하지 않습니다.
완전한 IEC 61850 의미 모델·서비스·적합성 인증 제품이 아닙니다.

| 형식 | 역할 |
|---|---|
| `IecDoublePoint` | Dbpos 2비트 상태 |
| `IecQuality` | IEC 품질 13비트 |
| `Iec61850Data` | MMS 값의 Dbpos/품질/UTC 해석 및 Byte 인코딩 |
| `MmsControlModel` | Direct/SBO normal/enhanced 제어 모델 구분 |
| `MmsControlValueKind` | 제한된 SPC/DPC/APC 값 프로파일 |

네임스페이스는 `VagabondK.nF.IEC61850`이며 `VagabondK.nF.MMS`와 함께 참조합니다.
품질과 시간 품질은 원시 의미 값이며 장비 상태의 정확성·시계 동기화·출력 안전성을 보장하지 않습니다.
공통 내부 제어 이름 검사 코드는 core 한 곳에서 컴파일하고 friend assembly 경계를 통해 Client/Server와 공유합니다.

## 역할 선택과 이전

| 역할 | 직접 참조 |
|---|---|
| 공통 값 변환 | MMS, IEC61850 |
| IEC Client | MMS, MMS.Client, IEC61850, IEC61850.Client |
| IEC Server/SCL 생성 모델 | MMS, MMS.Server, IEC61850, IEC61850.Server |
| TCP 사용 | 위 역할에 MMS.TCP 추가 |

`Iec61850Client`·`MmsControlClient`·`MmsReportClient`·`MmsDataSet`·`MmsReport`·`MmsControlError` 및 콜백 형식은
별도 `VagabondK.nF.IEC61850.Client` DLL과 같은 이름의 네임스페이스로 이동했습니다.
이전 Client 앱은 core 참조를 유지하고 `.Client` 참조와 `using VagabondK.nF.IEC61850.Client;`를 추가하십시오.
일반 MMS Client도 `VagabondK.nF.MMS.Client`로 이동했습니다.
Server 앱에는 두 Client DLL이 필요하지 않습니다. 호환성을 위한 클라이언트 중복 구현이나 형식 전달자는 남기지 않습니다.

지원 서비스·예제·상호운용 및 과거 측정 기록은 [Client](../VagabondK.nF.IEC61850.Client/README.md)와
[Server](../VagabondK.nF.IEC61850.Server/README.md)를 참조하십시오.
분리 구조·비용·검증 기록은 [MMS](../../MMS/VagabondK.nF.MMS/README.md)에 있습니다.
이전 장시간 시험의 수치를 새 분리 이미지의 내구성 근거로 사용하지 않습니다.
