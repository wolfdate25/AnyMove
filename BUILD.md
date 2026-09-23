# AnyMove 빌드 방법

## 필요 도구

- .NET 8 SDK
- Inno Setup 6 (`ISCC`가 PATH에 있어야 인스톨러 빌드 가능)

## 빌드

저장소 루트(`AnyMove/`)에서 실행한다.

```powershell
# 단일 실행 파일 게시
dotnet publish src/AnyMove/AnyMove.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o dist/app

# 인스톨러 생성
ISCC installer/anymove.iss
```

산출물: `dist/AnyMove-Setup-0.1.12.exe`

## 수동 실행 테스트

게시된 `AnyMove.exe`를 관리자 권한으로 직접 실행하면 인스톨러 없이 동작을 확인할 수 있다.
`Win + 좌드래그`로 창이 이동하고, 시작 메뉴가 뜨지 않아야 한다.

## 주의

- 매니페스트가 `requireAdministrator`이므로 실행 시 UAC 동의가 필요하다.
- 평소 사용은 인스톨러가 등록하는 작업 스케줄러 로그온 태스크 경유를 권장한다.
- 실행 파일·인스톨러에 코드 서명이 없으므로 첫 실행 시 Windows SmartScreen 경고가 나올 수 있다. 게시자가 직접 실행한 파일임을 확인한 뒤 "추가 정보 → 실행"을 선택한다.
