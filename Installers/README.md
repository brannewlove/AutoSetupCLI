# Installers — 설치 파일 배치 폴더

`apps_config.json`에 설정된 `{BaseDir}\Installers\...` 경로에 대응하는 폴더입니다.
빌드 또는 게시(`dotnet build` / `dotnet publish`) 시 이 폴더 안의 모든 파일이 출력 폴더로 자동 복사됩니다.

## 기본 설정(`apps_config.json`) 기준 배치해야 할 파일 위치

```text
Installers/
├── Privacy-i/
│   ├── setup.exe          # Privacy-i v6.0 Agent 설치 파일
│   └── privacy.iss        # InstallShield 무인 응답 파일
│
├── Fasoo/
│   ├── setup.exe          # Fasoo DRM Client 설치 파일
│   └── fasoo.iss          # InstallShield 무인 응답 파일
│
├── PC-OFF/
│   └── setup.exe          # PC-OFF 근무시간 관리 설치 파일 (InnoSetup)
│
└── Sindoh/
    └── KOBF7JA_.INF       # SINDOH 복합기 드라이버 INF 파일 (및 관련 드라이버 파일 일체)
```

