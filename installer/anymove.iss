; AnyMove Inno Setup 스크립트
; 빌드: ISCC anymove.iss (출력: ..\dist)

#define AppVersion "0.1.13"

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
; 설치 직후 실행(체크박스 기본 선택). 인스톨러가 이미 상승됐으므로 UAC 추가 동의 없이 뜬다.
; nowait 필수: 상주 앱이라 종료를 기다리면 설치가 끝나지 않는다.
; shellexec 필수: CreateProcess는 UAC 승격을 못 해서 740 오류가 나므로,
; ShellExecute로 띄워 매니페스트 승격을 타게 한다. 조용한 설치에서는 띄우지 않는다.
Filename: "{app}\AnyMove.exe"; Description: "AnyMove 실행"; Flags: postinstall nowait shellexec skipifsilent

[UninstallRun]
Filename: "schtasks"; Parameters: "/Delete /TN ""AnyMove"" /F"; Flags: runhidden; RunOnceId: "RemoveAnyMoveTask"

[Code]
// 실행 중인 AnyMove를 설치·제거 전에 자동 종료한다.
// 우아한 종료(WM_CLOSE) 시도 후 남아 있으면 강제 종료한다.
// 실행 중이 아니어도 실패는 무시하고 계속한다(파일 사용 중 대화상자가 대신 뜬다).
procedure KillRunningApp;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM AnyMove.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM AnyMove.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    KillRunningApp;
end;

procedure CurUninstallStepChanged(CurStep: TUninstallStep);
begin
  if CurStep = usUninstall then
    KillRunningApp;
end;
