# AnyMove 기술 설계 문서

## 1. 개요

AnyMove는 윈도우 전역에서 조합 키 + 마우스 드래그로 어떤 창의 어느 영역을 눌러도 창을 이동시키는 상주형 유틸리티다.
상세 배경과 목표는 [plan.md](../plan.md)을 따른다. 본 문서는 구현을 위한 기술 설계를 정의한다.

## 2. 아키텍처

단일 프로세스, 3개 모듈 구성:

```
[Hook Thread] ---> [Move Engine] ---> [Win32 User32 API]
      |                   |
      +---> [Tray/Config (UI 스레드)]
```

- **Hook Thread**: 저수준 마우스(`WH_MOUSE_LL`) + 키보드(`WH_KEYBOARD_LL`) 후크와 메시지 펌프 전용 스레드. 입력 이벤트 판정과 이동 모드 상태머신 담당.
- **Move Engine**: 대상 창 확정, 이동 가능 여부 판정, 좌표 계산, `SetWindowPos` 호출 담당. UI 스레드와 상태를 공유하지 않고, 후크 스레드에서 동기 실행한다.
- **Tray/Config**: 트레이 아이콘, 사용/중지, 조합 키 변경, 자동 시작, 버전 표시(GitHub 릴리스 연결), 설정 파일 저장/로드. 이동 경로와 분리되어 후크 안정성에 영향을 주지 않는다.
- 프로세스는 상승 권한으로 실행한다(`requireAdministrator` 매니페스트). 관리자 권한 창 이동을 위한 전제이며, 상세는 8절을 따른다.

프로세스는 DPI 인식을 명시적으로 설정한다. 프로젝트 속성 `ApplicationHighDpiMode=PerMonitorV2`를 사용한다(매니페스트 방식은 WinForms에서 무시되므로 사용하지 않는다). 후크 좌표·`GetWindowRect`·`SetWindowPos`가 모두 물리 픽셀로 일치해야 창이 커서를 정확히 따라간다.

## 3. 입력 처리 설계

### 3.1 후크 설치

- `SetWindowsHookEx`로 `WH_MOUSE_LL` 마우스 후크와 `WH_KEYBOARD_LL` 키보드 후크를 설치한다. 키보드 후크는 `Win` 키-업 처리(시작 메뉴 억제)와 이동 중 `Esc` 취소용이다.
- 후크 프로시저는 전용 스레드의 메시지 펌프에서 동작한다. 설치 실패 시 시작 단계에서 예외로 보고하고, 메시지 펌프 종료 조건은 `GetMessage > 0`이다.
- 조합 키(기본값 `Win`) 상태는 후크 콜백 시점에서 `GetAsyncKeyState`로 확인한다. `Win` 키 이벤트는 삼키지 않고 항상 통과시키므로 OS 상태와 물리가 일치한다.

### 3.2 상태머신

```
Idle --(조합 키 + LButtonDown / NCLButtonDown[HTCAPTION])--> Moving (이동 가능 창일 때)
Idle --(이동 불가 창)--> Idle (이벤트 그대로 전달)
Moving --(MouseMove / NCMouseMove)--> Moving (SetWindowPos 후 통과)
Moving --(LButtonUp / NCLButtonUp | Esc | 조합 키 릴리스)--> Idle
```

- 진입 시점에 `WindowFromPoint` → 최상위 창 승격으로 대상을 확정하고 시작 좌표를 스냅샷한다. 타이틀바 드래그(`NCLBUTTONDOWN`)는 hit-test가 `HTCAPTION`일 때만 진입하고, 닫기·최대화 등 캡션 버튼은 그대로 전달한다.
- `Moving` 구간에서는 버튼 down/up을 삼켜 원본 클릭이 앱에 전달되지 않게 한다. 이동 메시지는 커서 갱신을 위해 통과시킨다(down을 못 받은 앱은 move만으로 클릭·드래그 오동작을 일으키지 않는다). 릴리스가 다른 창의 비클라이언트 영역에서 일어나면 `NCLBUTTONUP`으로 오므로 종료 조건에 포함한다.
- 이동이 시작되지 않은 경우는 원래 이벤트를 그대로 전달해 일반 클릭 동작을 보존한다.
- `Win` 키 이벤트(down/up)는 삼키지 않고 항상 통과시킨다. 셸이 보는 쌍이 물리와 항상 일치하므로 상태 고착·팬텀 조합이 구조적으로 불가능하다. 일반 탭의 시작 메뉴와 `Win+R` 등 모든 조합은 손대지 않고 그대로 동작한다.
- 실제 드래그가 끝난 뒤의 조합 키 `up`에서만 무해한 `F24` 탭 1회를 `SendInput`으로 넣어 눌렀던 흔적을 지운다(`Win`은 시작 메뉴 방지, `Alt`는 단독 `up` 시 메뉴 바 포커스 방지). 실제 `up`은 통과시켜 래치를 푼다. 순서가 뒤바뀌어도 시작 메뉴/메뉴 포커스가 생길 뿐 고착·팬텀 조합은 생기지 않는다(안전한 방향으로만 실패). `F24`는 바인딩이 사실상 없고 고정 키 대화상자도 유발하지 않아 선택했다.
- `Esc`, 버튼 릴리스, 조합 키 릴리스 중 하나라도 발생하면 이동을 종료하고 후크 억제를 해제한다.

## 4. 창 판정 설계

### 4.1 대상 창 확정

1. 커서 스크린 좌표에서 `WindowFromPoint`로 핸들을 얻는다.
2. 자식 컨트롤(버튼, 패널 등)일 수 있으므로 `GetAncestor(hWnd, GA_ROOT)`로 최상위 창으로 승격한다.
3. `GetWindowRect`로 창 사각형과 커서 오프셋(`cursor - rect.left/top`)을 저장한다.

### 4.2 이동 가능 여부 필터

다음 중 하나라도 해당하면 이동 모드에 진입하지 않는다:

- `IsZoomed(hWnd)`가 참인 최대화 창. 드래그 도중 최대화된 경우도 매 이동마다 재검사해 중단한다.
- 전체화면 창(모니터 작업 영역과 창 Rect 비교, 메뉴·테두리 없는 최상위 창).
- 시스템 지정 제외 창(클래스 기준): `Shell_TrayWnd`, `Shell_SecondaryTrayWnd`(작업 표시줄), `DV2ControlHost`, `Windows.UI.Core.CoreWindow`(시작 메뉴·위젯), `MultitaskingViewFrame`(작업 보기), `Progman`, `WorkerW`(바탕 화면).
- 관리자 권한 창은 제외하지 않는다. 프로세스가 상승 권한으로 실행되므로 정상 이동 대상이다. 비상승 실행(예: 수동으로 권한 없이 실행한 경우)으로 고권한 창 조작이 거부되면 무시하고, 트레이에 제한 모드 상태를 표시한다.

## 5. 이동 엔진 설계

- 입력: 이동 시작 시 `GetWindowRect` 스냅샷 + 커서 오프셋.
- 출력: 마우스 이동 델타를 더한 새 좌표 `(newX, newY)`.
- 호출: `SetWindowPos(hWnd, NULL, newX, newY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)`.
  - 크기·Z-order·활성화 상태를 바꾸지 않고 위치만 이동한다.
- 멀티모니터: 스크린 좌표계 그대로 사용한다. 모니터 경계를 클램프하지 않고, 커서가 있는 모니터로 자연스럽게 넘어가게 한다.
- DPI: 프로세스 DPI 인식을 선언해 `GetWindowRect`/`SetWindowPos` 좌표계 불일치를 방지한다. 100%/150% 혼합 환경은 검증 계획에 포함한다.

## 6. 설정·트레이 설계

- 설정 파일: `%AppData%\AnyMove\settings.json`.
  - `modifier`: `"Win"` (기본값, `"Alt"`로 변경 가능).
  - `enabled`: `true/false`.
  - `autostart`: `true/false`. 시작 시 실제 스케줄러 등록 상태와 동기화해 표시한다.
- 트레이 메뉴: 사용/중지 토글, 조합 키 변경, 자동 시작, 버전 표시(GitHub 릴리스 페이지 연결), 종료. 비상승 실행 시 제한 모드(관리자 창 미지원) 표시를 포함한다.
- 트레이 아이콘은 실행 파일에 박힌 아이콘(`Assets\app.ico`)을 그대로 사용한다.
- 로그온 시 탐색기보다 먼저 뜨면 아이콘 등록이 실패하므로, 시작 시 작업 표시줄 존재를 확인하고 없으면 숨긴 채로 둔다. 메시지 전용 창으로 `TaskbarCreated`를 받으면 아이콘을 통째로 다시 만든다(탐색기 재시작에도 대응).
- 자동 시작: 작업 스케줄러 로그온 트리거(가장 높은 권한 사용)로 등록하고, 인스톨러 제거 시 함께 해제한다.
- 설정 변경은 후크 재설치 없이 다음 입력 이벤트부터 적용한다.

## 7. 예외·안정성 설계

- 후크 콜백 내 예외는 이동 모드 강제 종료 + 이벤트 전달(패스스루)로 처리한다. 입력 먹통을 최우선으로 방지한다.
- `SetWindowPos` 실패 시 이동을 중단하고 일반 클릭으로 폴백한다.
- 이동 종료 조건(버튼 릴리스 양쪽 메시지, 조합 키 릴리스, `Esc`, 사용 중지)이 겹겹이 있어 이동 모드가 비정상 잔류하지 않게 한다. 별도 타이머 감시는 두지 않는다.
- 후크 설치 실패 시 시작 단계에서 안내 후 종료한다.
- 킬 스위치: 트레이 중지 + 프로세스 종료만으로 시스템이 원래 상태로 복귀한다.

## 8. 빌드·배포

- 구현 언어: C#(.NET 8, WinForms), 단일 실행 파일 게시(`dotnet publish -r win-x64 --self-contained`). 인스톨러: Inno Setup 6. 무설치 포터블 ZIP도 제공한다(자동 시작 등록 없이 exe 직접 실행, 실행마다 UAC 동의).
- 앱 매니페스트에 `requireAdministrator`를 선언한다. 수동 실행 시 UAC 동의가 필요하다.
- 인스톨러(`installer/anymove.iss`, 게시 출력 `dist/app` 기준): 고정 `AppId`, 64비트 설치 모드, 모던 마법사, 실행 파일 아이콘 사용, 한국어/중국어(간체, 저장소 내 `installer/Languages` 동봉)/영어 선택. 작업 스케줄러에 로그온 태스크(가장 높은 권한)를 생성해 평소 사용 시 UAC 프롬프트 없이 시작되게 한다. 제거 시 태스크를 함께 삭제한다.
- 미서명 실행 시 Windows SmartScreen 경고가 나올 수 있음을 배포 문서에 명시한다.

## 9. 검증 계획

- 핵심 E2E: 메모장·브라우저·탐색기에서 `Win + 본문 드래그` 이동 확인, 일반 클릭 정상 동작 대조, 시작 메뉴가 뜨지 않는지 확인.
- 관리자 권한 검증: 관리자 권한 메모장 등 상승 프로세스 창 이동 확인, 비상승 실행 시 무시 및 제한 모드 표시 확인.
- 최고 위험 항목 우선: 이동 중 원본 클릭 억제 여부(텍스트 선택·버튼 오작동 여부).
- 경계: 최대화, 전체화면, 관리자 권한 앱, 멀티모니터, DPI 혼합 환경.
- 안정성: 수 시간 상주 후 CPU·메모리 증가 없음, 후크 해제 후 입력 복귀.

## 9-2. 검증 결과(0.1.7 기준, 실기 확인)

- `Win`/`Alt + 본문·버튼 영역 드래그` 이동, 일반 클릭 보존, 시작 메뉴 미표시, 팬텀 `Win` 조합 없음 확인.
- 관리자 권한 창 이동 확인.
- 인스톨러 3개 국어 선택 확인.
- 0.1.8 추가분(트레이 버전 항목의 GitHub 연결)은 클릭 동작 확인이 남음.

## 10. Sources

- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-windowfrompoint
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getancestor
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-iszoomed
- https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process
