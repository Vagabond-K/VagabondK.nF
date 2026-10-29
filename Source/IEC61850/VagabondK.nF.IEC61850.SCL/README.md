# PC 전용 SCL 정적 모델 생성기

제한된 IEC 61850 SCL을 **C# 7.3 정적 Server 모델**로 변환하는 .NET 10 CLI입니다.
MCU에서 XML을 파싱하지 않으며 MCU DLL/NuGet에 이 도구를 포함하지 않습니다.
전체 SCL을 가져오는 도구나 IEC 61850-6 적합성 검증기, Logical Node 의미 검증기는 아닙니다.

```powershell
dotnet run -c Release --project .\Source\IEC61850\VagabondK.nF.IEC61850.SCL `
  -- .\Tests\IEC61850\VagabondK.nF.IEC61850.Tests\Fixtures\Data.scd .\bin\GeneratedIec61850Model.cs
```

생성된 `GeneratedIec61850Model`을 MCU 앱에 Compile로 포함하고 MMS core/Server와 IEC core/Server 프로젝트를 참조하십시오.
두 Client 프로젝트/DLL은 필요하지 않습니다.
생성 코드는 `Iec61850ServerSession`과 `Iec61850ServerData`를 사용하며 일반 변수 형식은 MMS Server에서 가져옵니다.
각 `V0`… 필드는 선언 순서의 말단 데이터 항목(leaf)이며 생성문에 원본 domain/item 이름을 유지합니다.
`Variables`·`DataSets`·`Reports`·`Controls` 배열과 `CreateSession(IMmsChannel)`을 제공합니다.
앱에서 로컬 setter로 값을 연결하고, 보고 사용 시 `ProcessReports()` 이벤트 루프도 구현해야 합니다.
모델 인스턴스마다 독립된 메모리를 사용하며 원격 쓰기를 자동으로 허용하지 않습니다.

## 지원하는 입력

| 구성 | 제한 |
|---|---|
| 문서 | IEC SCL 네임스페이스 `http://www.iec.ch/61850/2003/SCL`, 최대 2 Mi 문자, DTD/외부 엔터티 금지 |
| 모델 | 정확히 한 IED/AccessPoint/Server, 복수 LDevice·LN, 각 LDevice에 LN0=LLN0 한 개 |
| 이름 | ASCII 영숫자/밑줄, MMS domain/item 최대 64자, domain은 ldName 또는 IED.name+LDevice.inst |
| 템플릿 | LNodeType/DOType/DAType, DO·SDO·DA·Struct/BDA 재귀 전개, 최대 깊이 16·순환 거절 |
| scalar | BOOLEAN/INT32/유한 FLOAT32/Dbpos/Enum/Quality/Timestamp, FC=ST/MX/SP/CF |
| Enum | EnumType/EnumVal의 중복 없는 label·Int32 ordinal, 선언된 label 또는 ordinal 초기값만, 생략 시 선언된 0 필요 |
| 제어 | 중첩되지 않은 SPC/DPC/Float32 APC, CF Enum ctlModel=1..4, 대응하는 CO 명령 구조 검사, 기본 비활성 |
| 초기 값 | scalar DA/BDA의 단일 Val, DOI/SDI/DAI leaf Val 우선, BOOLEAN은 true/false, Dbpos는 intermediate-state/off/on/bad-state |
| 품질·시각 | 기본 Quality=Good, Timestamp epoch=0·품질=0x3f(시계 미동기화·정밀도 미지정), 초기 Val은 거절하고 앱 setter로 지정 |
| DataSet | 최대 8개, 각 1~16개 FCDA, 명시한 doName/daName이 같은 IED의 생성 leaf에 정확히 일치해야 함 |
| ReportControl | 최대 4개 URCB, 비indexed·BufTm=0·RptEnabled.max=1, 최소 송신 간격 100ms |
| trigger/options | dchg/qchg/period/gi=true·dupd=false, seqNum/reasonCode/dataSet/configRef=true만, URCB 멤버에 명시적 dchg(Quality는 qchg) 필요 |
| 최종 상한 | URCB별 생성 변수 12개와 제어별 런타임 생성 이름을 합쳐 최대 128개 |

복합 DA는 **MMS leaf 이름으로 평탄화**하며 상위 FC/DO 구조체 이름은 자동 등록하지 않습니다.
DataSet의 전체 DO/FC wildcard·배열 인덱스·alternate access·cross-IED 참조는 없습니다.
배열 count·다른 scalar 형식·SettingGroup·LogControl·BRCB·GOOSE·SV·Communication/Substation 설정도 변환하지 않습니다.
지원하지 않는 요소/속성·잘못된 참조/값·중복·순환은 행 번호가 있는 오류로 거절하며 일부 모델만 성공한 것처럼 출력하지 않습니다.
Header는 메타데이터이며 통신 모델을 생성하지 않습니다. 사용하지 않는 타입 템플릿은 전개하지 않습니다.
bufTime 이외의 버퍼형 보고·시간예약·물리 출력 드라이버·네트워크/보안 설정은 생성하지 않습니다.
새 코드 전체를 검증한 뒤 출력 파일을 교체하므로 입력 오류가 기존 출력 파일을 덮어쓰지 않습니다.

PC 회귀에서 예제 SCD의 생성 코드를 실제 C# 7.3으로 컴파일하고 생성 모델/세션,
초기 값·참조·미지원 형식·누락·순환·DTD 거절을 검사합니다.

## 제어 템플릿의 제한

`Fixtures\Data.scd`는 DPC DirectNormal/SboNormal/DirectEnhanced와 Float32 APC SboEnhanced의 예제입니다.
제어 DO에 CO DA가 있을 때만 제어 객체를 생성합니다.
SPC/DPC의 `stVal`은 ST BOOLEAN/Dbpos, APC의 `mxVal`은 MX Struct의 단일 FLOAT32 `f`여야 합니다.
`ctlModel`은 CF Enum이며 DOI/DAI의 초기값이 타입 기본값보다 우선합니다.
선택 모델의 `sboTimeout`은 CF INT32U 1..60000ms(생략 시 1000),
`sboClass`는 CF Enum operate-once=0만 제공합니다.
`Oper`/`Cancel` 및 모델에 따른 `SBO`(VisString129)/`SBOw`가 필요합니다.
명령 Struct는 ctlVal·origin(orCat Enum/orIdent Octet64)·ctlNum INT8U·T Timestamp·Test BOOLEAN,
Oper/SBOw에는 Check까지 순서가 일치해야 합니다.
APC ctlVal은 FLOAT32 `f`만 가진 Struct입니다. 시간예약/정수형 APC는 거절합니다.

제어 상태·명령·설정 이름은 런타임 `MmsServerControl`이 생성합니다.
따라서 이 이름을 중복 leaf로 생성하지 않으며 `Variables`/`Vn`에도 포함하지 않습니다.
제어 상태 초기값은 런타임 기본값(SPC=false, DPC=Intermediate, APC=0)이며 별도 Val은 거절합니다.
제어 상태를 DataSet 멤버로 직접 참조하는 SCL 변환은 아직 제공하지 않습니다.
별도 q/t leaf는 생성할 수 있지만 제어 실행과 품질·시각 갱신을 자동 연결하지 않습니다.
Enum은 Int32 MMS leaf이며 label 검증은 생성 시에만 수행합니다. 로컬 SetInteger의 값 범위를 자동 제한하지 않습니다.

생성된 `Controls`는 **모두 Enabled=false**입니다.
애플리케이션이 허용 검사와 실행 처리기를 연결하고 명시적으로 활성화해야 합니다.
enhanced 모델에는 앱의 완료 정책과 `ProcessControlCompletion()` 처리가 필요합니다.
생성 코드는 설비를 자동 구동하거나 실제 결과 없이 성공을 통지하는 완료 처리기를 등록하지 않습니다.
PC 회귀에서 생성 코드 자체를 컴파일해 네 제어 모델의 실행·Test 무변경·완료와
Enum·명령 타입/순서·SBO 설정 오류를 검사합니다.
로컬 설치한 도구의 생성 출력도 실제 nanoFramework Release 컴파일·minimizer·배포 BIN 생성을 통과했습니다
(`bin\iec61850-scl-generated-nano.log`). 이 검사는 생성 모델 앱의 보드 배포/실행을 뜻하지 않습니다.
Client 역할 분리 후에는 `bin\nuget\role-split-scl`에 새로 패키징한 도구의 로컬 설치·생성,
생성 모델의 MMS/IEC core·Server 참조와 Debug/Release nanoFramework 컴파일·배포 BIN 생성을 확인했습니다.
이 검증에서도 두 Client DLL을 참조하거나 배포 출력에 포함하지 않았습니다.

## PC 도구 로컬 패키지

```powershell
.\Build\Pack-Iec61850SclTool.ps1 -OutputPath .\bin\nuget\iec61850-scl-local
```

`VagabondK.nF.IEC61850.SCL.0.1.0-preview.1.nupkg`는 .NET 10 PC 전용 dotnet tool입니다.
새 출력 폴더에 생성한 뒤 외부 NuGet 소스를 차단한 로컬 설치와 예제 SCD 변환을 확인합니다.
설치 후 실행 명령은 `vk-nf-iec61850-scl <input.scl> <output.cs>`입니다.
이 도구와 native 검증 피어는 MMS/IEC MCU 라이브러리 7개 패키지에 포함되지 않습니다. 자동 업로드하지 않습니다.
