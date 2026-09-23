; AnyMove Inno Setup 스크립트
; 빌드: ISCC anymove.iss (출력: ..\dist)

#define AppVersion "0.1.7"

[Setup]
AppId={{063092CA-D282-4782-AAD6-428171CAAD52}
AppName=AnyMove
AppVersion={#AppVersion}
DefaultDirName={autopf}\AnyMove
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=AnyMove-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\AnyMove\Assets\app.ico
UninstallDisplayName=AnyMove
UninstallDisplayIcon={app}\AnyMove.exe

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; BUILD.md의 게시 출력(dist\app)과 일치해야 한다.
Source: "..\dist\app\AnyMove.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\AnyMove"; Filename: "{app}\AnyMove.exe"

; 자동 시작: 로그온 태스크(가장 높은 권한) 등록 -> UAC 프롬프트 없이 상승 실행
[Run]
Filename: "schtasks"; Parameters: "/Create /TN ""AnyMove"" /TR ""\""{app}\AnyMove.exe\"""" /SC ONLOGON /RL HIGHEST /F"; Flags: runhidden

[UninstallRun]
Filename: "schtasks"; Parameters: "/Delete /TN ""AnyMove"" /F"; Flags: runhidden; RunOnceId: "RemoveAnyMoveTask"
