# AutoSetupCLI — 사내 필수 프로그램 무인 자동 설치 CLI

**AutoSetupCLI**는 사내 필수 보안 솔루션, 업무용 프로그램, 복합기(프린터) 드라이버 등을 JSON 설정 기반으로 일괄 자동(Silent) 설치하고 환경을 구성하는 **.NET 8.0 (Windows)** 기반 명령줄 도구입니다.

---

## 1. 프로젝트 파일 구조

```text
AutoSetupCLI/
├── AutoSetupCLI.csproj            # .NET 8.0 Windows 프로젝트 설정 파일
├── Program.cs                     # CLI 진입점, 인자 파싱, 전체 설치 흐름 제어 및 결과 요약 출력
├── apps_config.json               # 실제 실행 시 로드되는 설치 대상 프로그램 정의 파일
├── apps_config.template.json      # 새로운 프로그램 추가 시 참고할 수 있는 기본 템플릿 파일
│
├── Configuration/                 # JSON 설정 매핑용 데이터 모델 (DTO / Enum)
│   ├── AppConfig.cs               # 개별 설치 프로그램의 설정 모델 (순서, 엔진, 경로, 감지 규칙, 훅 등)
│   ├── InstallerEngineType.cs     # 설치 엔진 타입 열거형 (Msi, InstallShield, InnoSetup, Nsis, PrinterDriver 등)
│   ├── DetectionRule.cs           # 기설치 여부 및 사전 조건 감지 규칙 모델 (Registry, File, Service, Process, Command)
│   ├── HookAction.cs              # 설치 전/후 실행할 훅(Hook) 액션 모델 (레지스트리 수정, 파일 제어, 서비스 제어 등)
│   └── PrinterDriverConfig.cs     # 복합기/프린터 드라이버 및 포트·부서코드(사번) 등록 전용 설정 모델
│
├── Services/                      # 핵심 비즈니스 로직 및 실행 서비스
│   ├── ExecutionEngine.cs         # 프로그램별 설치 파이프라인 실행 (감지 → Pre-Hook → 설치 → Post-Hook)
│   ├── EnvironmentDetector.cs     # 레지스트리, 파일 버전, 서비스 상태, 프로세스 등을 검사하여 기설치 여부 판별
│   ├── ParameterResolver.cs       # {BaseDir}, {UserId}, {PrinterIp} 등 동적 매개변수 및 환경변수 치환기
│   ├── IssTemplateEngine.cs       # InstallShield 응답 파일(.iss) 템플릿의 동적 변수를 치환하여 임시 응답 파일 생성
│   ├── PrinterManager.cs          # pnputil 및 PowerShell을 이용한 프린터 포트/드라이버 설치 및 사번 주입
│   └── Logger.cs                  # 콘솔 컬러 출력 및 파일 로그(logs/Install_yyyyMMdd_HHmmss.log) 동시 기록
│
└── Installers/                    # 실제 설치 파일(.exe, .msi, .inf, .iss 등)을 넣는 폴더 (빌드 시 출력 폴더로 자동 복사됨)
    ├── README.md                  # 각 폴더별 넣어야 할 설치 파일 안내
    ├── Privacy-i/                 # setup.exe, privacy.iss 배치
    ├── Fasoo/                     # setup.exe, fasoo.iss 배치
    ├── PC-OFF/                    # setup.exe 배치
    └── Sindoh/                    # KOBF7JA_.INF 및 드라이버 파일 배치
```

> **💡 헷갈리지 않는 파일 수정 기준**
> - **수정할 설정 파일**: 루트의 **[`apps_config.json`](apps_config.json)** 하나만 수정하시면 됩니다. (`apps_config.template.json`은 새 항목 추가 시 참고용 샘플입니다.)
> - **설치 파일 넣는 곳**: 루트의 **`Installers/`** 하위 폴더에 넣으시면 빌드/배포 시 실행 파일 옆으로 자동 복사됩니다.
> - **`graft/` 폴더**: 코드 검색 도구용 자동 인덱스 파일이므로 신경 쓰지 않으셔도 됩니다.

---

## 2. 핵심 동작 원리 (설치 파이프라인)

`AutoSetupCLI.exe`가 실행되면 [`Program.Main`](Program.cs)에서 설정을 읽고 각 앱에 대해 [`ExecutionEngine.ExecuteAppAsync`](Services/ExecutionEngine.cs)를 순서(`Order`)대로 호출합니다.

1. **매개변수 바인딩 (`ParameterResolver`)**: CLI 인자(`--user-id`, `--printer-ip` 등) 및 시스템 기본 변수(`{BaseDir}`, `{MachineName}` 등)를 메모리에 등록합니다.
2. **기설치 환경 감지 (`EnvironmentDetector`)**: 각 앱의 `Detection` 규칙을 평가합니다. 이미 조건이 충족되면 설치를 건너뜁니다(`Skipped`). (`--force` 옵션 시 무시)
3. **사전 작업 실행 (`PreInstallHooks`)**: 설치 전 필요한 레지스트리 설정, 프로세스 종료, 서비스 중지, 명령 실행 등을 수행합니다.
4. **설치 실행 (`ExecutionEngine` / `PrinterManager`)**:
   - 일반 설치 파일(`Msi`, `InstallShield`, `InnoSetup`, `Nsis`, `Executable`, `PowerShell`)은 지정된 사일런트 인자와 함께 프로세스를 실행하고 종료 코드(`ExitCode`)와 타임아웃을 감시합니다.
   - `InstallShield` 엔진에서 `IssTemplatePath`가 지정된 경우, `IssTemplateEngine`이 템플릿 내 변수를 치환한 임시 `.iss` 파일을 생성하여 `{IssFilePath}`에 바인딩합니다.
   - `PrinterDriver` 엔진은 `PrinterManager`를 통해 드라이버 스토어 등록(`pnputil`), TCP/IP 포트 및 프린터 장치 생성(PowerShell `Add-PrinterPort`, `Add-PrinterDriver`, `Add-Printer`), 사번/부서코드 레지스트리 주입을 자동 수행합니다.
5. **사후 작업 실행 (`PostInstallHooks`)**: 설치 성공 후 환경 설정 파일 배포, 레지스트리 값 주입, 서비스 시작 등을 처리합니다.
6. **결과 요약 및 리포트**: 전체 프로그램의 성공/스킵/실패 여부, 소요 시간, 재부팅 필요 여부(`ExitCode: 3010` 등)를 표 형태로 출력하고 로그 파일을 남깁니다.

---

## 3. 다른 환경에서 빌드 및 배포 패키지 만들기

다른 PC에서 소스 코드를 직접 수정·빌드하거나, **.NET이 전혀 설치되지 않은 일반 업무용 PC(초기화 PC, WinPE 등)**에서 바로 실행할 수 있는 독립 실행 파일을 만드는 방법입니다.

### 3.1 다른 개발 PC에서 소스 코드를 직접 빌드할 때

1. **필수 설치 도구**: **.NET 8.0 SDK** 이상 설치
   - Windows PowerShell에서 아래 명령어로 한 번에 설치하거나 [Microsoft .NET 다운로드 페이지](https://dotnet.microsoft.com/download/dotnet/8.0)에서 설치합니다:
     ```powershell
     winget install Microsoft.DotNet.SDK.8
     ```
2. **소스 복제 및 빌드**:
   ```powershell
   # 1. 프로젝트 폴더로 이동
   cd AutoSetupCLI

   # 2. NuGet 패키지 복원 및 빌드
   dotnet restore
   dotnet build -c Release
   ```

### 3.2 .NET이 없는 대상 PC용 '단일 실행 파일(Single-File)' 배포본 만들기 (권장)

실제 설치 대상 PC(신규 입사자 PC 등)에는 .NET SDK나 런타임이 없는 경우가 많습니다. 아래 명령어로 게시(Publish)하면 **.NET 런타임이 내부에 포함된 단 하나의 `AutoSetupCLI.exe`**가 `./publish` 폴더에 생성됩니다.

```powershell
# 64비트 Windows용 독립 실행 단일 파일(.exe) 생성
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```

> **실행 결과 (`./publish` 폴더 구성)**:
> ```text
> publish/
> ├── AutoSetupCLI.exe       # .NET 설치 없이 어디서나 바로 실행 가능한 단일 실행 파일
> ├── apps_config.json       # 설정 파일 (자동 복사됨)
> └── Installers/            # 설치 파일 폴더 (자동 복사됨)
> ```
> 이 **`publish` 폴더 통째로 USB나 네트워크 공유 폴더에 담아서 다른 PC로 가져가시면** 아무런 사전 설치 없이 곧바로 실행됩니다.
> *(참고: 레지스트리(`HKLM`), 윈도우 서비스 제어, 프린터 드라이버 설치 등을 수행하므로 실행 시 **관리자 권한(Administrator)**이 필요합니다.)*

### 3.2 CLI 명령줄 옵션

```text
AutoSetupCLI.exe [옵션]
```

| 옵션 | 단축 | 설명 | 기본값 / 예시 |
| :--- | :--- | :--- | :--- |
| `--user-id <ID>` | `-u` | 사번 또는 사용자 ID (`{UserId}` 변수에 바인딩) | 미입력 시 대화형 프롬프트 표시 (`--silent` 제외) |
| `--printer-ip <IP>` | `-p` | 복합기/프린터 IP 주소 (`{PrinterIp}` 변수에 바인딩) | 기본값: `192.168.1.100` |
| `--office <Location>` | `-o` | 사업장 위치 (`{Office}` 변수에 바인딩) | 기본값: `HQ` (예: `Seoul`, `Busan`) |
| `--server-ip <IP>` | | 서버 IP 주소 (`{ServerIp}` 변수에 바인딩) | |
| `--silent` | `-s` | 무인/백그라운드 모드 (대화형 입력 비활성화) | `false` |
| `--dry-run` | `-d` | 시뮬레이션 모드 (실제 설치 및 레지스트리 변경 없이 감지 및 인자 치환만 검증) | `false` |
| `--force` | `-f` | 기설치 감지(`Detection`)를 무시하고 강제 재설치 | `false` |
| `--apps <k1,k2>` | `-a` | `apps_config.json` 내 특정 `Key`를 가진 프로그램만 선택 실행 | 예: `--apps fasoo,pcoff` |
| `--config <Path>` | `-c` | 사용자 정의 설정 JSON 파일 경로 지정 | 기본값: `{BaseDir}\apps_config.json` |
| `--log <Path>` | `-l` | 로그 파일 저장 경로 지정 | 기본값: `{BaseDir}\logs\Install_날짜_시간.log` |
| `--var <Key=Value>` | `-v` | 사용자 정의 동적 변수 추가 주입 (여러 번 사용 가능) | 예: `-v Dept=HR -v Branch=Suwon` |
| `--help` | `-h` | 도움말 출력 | |

### 3.3 주요 실행 예시

```powershell
# 1. 대화형 실행 (사번 입력 프롬프트가 뜨며 전체 필수 앱 설치)
AutoSetupCLI.exe

# 2. 완전 무인(Silent) 일괄 설치 (배치파일, WCD, 원격 배포용)
AutoSetupCLI.exe --user-id 20261101 --printer-ip 192.168.10.50 --silent

# 3. 사전 테스트 (Dry-Run: 실제 설치를 하지 않고 경로, 인자 치환, 기설치 여부만 시뮬레이션)
AutoSetupCLI.exe --user-id 20261101 --dry-run

# 4. 특정 프로그램만 선택하여 강제 재설치
AutoSetupCLI.exe --apps sindoh,fasoo --user-id 20261101 --force

# 5. 커스텀 변수와 별도 설정 파일 사용
AutoSetupCLI.exe -c custom_config.json -v DeptCode=DEV01 -v AuthServer=10.10.1.5 --silent
```

### 3.4 프로세스 종료 코드 (Exit Code)
- `0`: 모든 대상 프로그램 설치 완료 (또는 이미 설치되어 스킵됨)
- `2`: 설치 실패 항목이 1개 이상 존재함
- `3010`: 모든 설치는 성공했으나 시스템 재부팅이 필요함 (`RebootExitCodes` 해당 시)
- `1`: 설정 파일(`apps_config.json`) 누락 또는 JSON 파싱 오류

---

## 4. 설정 파일 (`apps_config.json`) 작성 가이드

설치할 프로그램 목록은 JSON 배열 형태로 정의합니다. 문자열 값 내부에 `{변수명}`을 넣으면 실행 시 동적으로 치환됩니다.

### 4.1 사용 가능한 동적 변수 (`ParameterResolver`)
- **시스템 자동 제공 변수**:
  - `{BaseDir}`: 실행 파일(`AutoSetupCLI.exe`)이 위치한 폴더 경로 (끝 `\` 제외)
  - `{MachineName}`: PC 호스트 이름
  - `{UserName}`: 현재 로그인한 Windows 계정명
  - `{UserDomain}`: 도메인 이름
  - `{SystemDrive}`: 시스템 드라이브 (예: `C:`)
  - `{ProgramFiles}`: `C:\Program Files`
  - `{ProgramFilesX86}`: `C:\Program Files (x86)`
  - `{Date}`: 실행 일자 (`yyyyMMdd`)
  - `{%환경변수명%}`: OS 시스템 환경 변수도 자동 확장됨
- **CLI 입력 변수**:
  - `{UserId}`, `{PrinterIp}`, `{Office}`, `{ServerIp}`, 그리고 `-v Key=Value`로 전달한 모든 `{Key}`
- **특수 런타임 변수**:
  - `{IssFilePath}`: `IssTemplatePath` 설정 시 동적으로 생성된 임시 `.iss` 파일의 전체 경로

### 4.2 지원 설치 엔진 (`Engine`) 및 기본 인자
`SilentArguments`를 비워두면 `Engine` 종류에 따라 아래 권장 사일런트 인자가 자동 할당됩니다:
- `"Msi"`: `msiexec.exe /i "설치파일" /qn /norestart` 실행
- `"InnoSetup"`: `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`
- `"Nsis"`: `/S`
- `"InstallShield"`: `-s` (`IssTemplatePath` 사용 시 `-s -f1"임시응답파일경로"`)
- `"Executable"`: 일반 `.exe` 직접 실행 (`SilentArguments`에 지정한 인자 그대로 전달)
- `"PowerShell"`: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "스크립트경로"` 실행
- `"PrinterDriver"`: `PrinterConfig` 객체를 사용하여 프린터 드라이버 및 포트 설치

### 4.3 기설치 감지 규칙 (`Detection`)
이미 설치된 프로그램을 건너뛰기 위한 조건입니다.
- `Type`: `"Registry"`, `"File"`, `"Service"`, `"Process"`, `"Command"`
- `Operator`: `"Exists"`, `"Equals"`, `"Contains"`, `"VersionGreaterOrEqual"`
- `Invert`: `true`로 설정하면 조건 결과를 반전합니다.

```json
"Detection": {
  "Type": "Registry",
  "RegistryKey": "HKLM\\SOFTWARE\\Somansa\\Privacy-i",
  "ValueName": "Version",
  "Operator": "Exists"
}
```
> **팁**: 64비트/32비트 레지스트리 뷰(`WOW6432Node`)를 자동으로 모두 탐색하므로, 32비트 앱이라도 기본 경로를 적어두면 자동 감지됩니다.

### 4.4 사전/사후 작업 훅 (`PreInstallHooks` / `PostInstallHooks`)
설치 전후에 추가 설정을 자동화할 수 있습니다.
- `Type`:
  - `"RegistrySet"`: `Target`(레지스트리 키), `ValueName`, `Value`, `ValueKind`(`"String"`, `"DWord"`, `"QWord"`)
  - `"RegistryDelete"`: `Target`(레지스트리 키), `ValueName`
  - `"FileWrite"`: `Target`(파일 경로), `Value`(파일 내용)
  - `"FileDelete"`: `Target`(삭제할 파일 경로)
  - `"ServiceControl"`: `Target`(서비스명), `ServiceAction`(`"Start"`, `"Stop"`, `"Restart"`)
  - `"RunCommand"`: `Target`(`cmd.exe /c`로 실행할 셸 명령어)

```json
"PostInstallHooks": [
  {
    "Type": "RegistrySet",
    "Target": "HKLM\\SOFTWARE\\MyCompany\\Agent",
    "ValueName": "EmployeeId",
    "Value": "{UserId}",
    "ValueKind": "String",
    "Description": "설치 후 사번 레지스트리 등록",
    "IgnoreErrors": true
  }
]
```

---

## 5. 프로그램별 개별 검증 및 에러 로그 진단 가이드

설치 파일들을 한꺼번에 실행하기 전에 **`--apps <키이름>`** 옵션과 **`--dry-run`** 옵션을 조합하면, 각 설치 파일별로 설정·경로·기설치 여부·에러 원인을 안전하게 하나씩 검증할 수 있습니다.

### 5.1 파일별 1:1 검증 명령어 (`--apps`)

#### Step 1: 설치 전 사전 검증 (`--dry-run` 시뮬레이션)
실제 설치를 하지 않고 **① 기설치 레지스트리 감지 결과**, **② `{UserId}` 등 매개변수 치환 결과**, **③ 실제 폴더에 설치 파일(`setup.exe`, `.inf`)이 있는지 여부**만 빠르게 검사합니다.

```powershell
# 1. Privacy-i 단독 사전 검증
dotnet run -- --apps privacy-i --user-id 20261109 --dry-run

# 2. Fasoo DRM 단독 사전 검증
dotnet run -- --apps fasoo --user-id 20261109 --dry-run

# 3. PC-OFF 단독 사전 검증
dotnet run -- --apps pcoff --user-id 20261109 --dry-run

# 4. 신도리코 복합기 단독 사전 검증 (프린터 IP 지정 테스트)
dotnet run -- --apps sindoh --user-id 20261109 --printer-ip 192.168.10.50 --dry-run
```

#### Step 2: 개별 프로그램 실제 사일런트 설치 검증
사전 검증에서 파일 경로와 인자가 정상으로 확인되면 `--dry-run`을 빼고 1개씩 실제 설치를 테스트합니다. (이미 설치된 PC에서 다시 테스트하려면 `--force` 추가)

```powershell
# 특정 프로그램 1개만 실제 무인 설치 실행
dotnet run -- --apps pcoff --user-id 20261109

# 이미 설치되어 있어도 무시하고 강제 재설치 테스트
dotnet run -- --apps pcoff --user-id 20261109 --force
```

---

### 5.2 에러 로그 확인 위치 및 문제 원인 파악 방법

실행이 끝나면 콘솔 하단에 **비고 / 오류 원인**이 요약 출력되며, 동시에 `logs/` 폴더 안에 **2종류의 로그 파일**이 자동 생성됩니다.

1. **통합 실행 로그 (`logs/Install_날짜_시간.log`)**:
   - 전체 진행 과정, 레지스트리 감지 상세 내역(`[DEBUG]`), 프로세스 표준 에러 출력(`stderr`), 종료 코드 원인 해설이 모두 기록됩니다.
2. **설치 엔진 전용 상세 로그 (`logs/{앱Key}_날짜_시간.engine.log`)**:
   - 각 설치 프로그램 엔진이 직접 기록한 내부 상세 로그입니다.
   - **InstallShield**: `-f2"로그경로"` 옵션이 자동 부여되어 생성되며, 실패 시 내부 `ResultCode`를 자동으로 읽어 원인을 분석해 줍니다.
   - **InnoSetup**: `/LOG="로그경로"` 옵션이 자동 부여되어 어느 파일 복사/레지스트리 작업에서 멈췄는지 기록됩니다.
   - **MSI**: `/L*v "로그경로"` 옵션이 자동 부여되어 상세 MSI 설치 로그가 남습니다.

#### 주요 실패 종료 코드(ExitCode)별 원인 및 해결책

| 엔진 구분 | 종료 코드 | 자동 분석되는 원인 및 해결 방법 |
| :--- | :---: | :--- |
| **공통 / 파일** | `-1` | **설치 파일 없음**: `Installers/` 하위 폴더에 `setup.exe` 또는 `.inf` 파일이 정확한 이름으로 들어있는지 확인하세요. |
| **공통 / 권한** | `5` | **액세스 거부**: PowerShell 또는 명령 프롬프트를 **관리자 권한으로 실행**했는지 확인하세요. |
| **InstallShield** | `-3` | **`.iss` 응답 파일 데이터 불일치**: 설치 화면 구성이 바뀌었거나 필수 입력값이 누락됨. 해당 PC에서 `setup.exe /r`을 실행해 수동 설치 후 `C:\Windows\setup.iss` 파일을 가져와 교체하세요. |
| **InstallShield** | `-5` | **`.iss` 파일 없음**: `SilentArguments`의 `-f1"경로"`에 지정된 `.iss` 응답 파일이 실제로 존재하는지 확인하세요. |
| **InstallShield** | `-12` | **대화상자 순서 불일치**: 이미 프로그램이 설치된 상태에서 제거/복구 화면이 떠서 `.iss` 응답 순서와 어긋난 경우입니다. |
| **MSI / 일반** | `1603` | **치명적 설치 오류**: 이미 상위 버전이 설치되어 있거나, 선행 조건(VC++ 재배포 패키지 등) 누락, 또는 파일이 사용 중일 때 발생합니다. `.engine.log`에서 `Return value 3` 위쪽 줄을 확인하세요. |
| **MSI** | `1618` | **중복 설치 실행 중**: 윈도우에서 다른 `msiexec.exe` 설치가 이미 진행 중입니다. 잠시 후 다시 실행하세요. |
| **공통** | `3010` | **성공 (재부팅 필요)**: 오류가 아니며 정상 설치되었습니다. 재부팅 후 적용됩니다. |


