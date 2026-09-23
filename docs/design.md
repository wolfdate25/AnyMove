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

- **Hook Thread**: 저수준 마우스 후크 + 메시지 펌프 전용 스레드. 입력 이벤트 판정과 이동 모드 상태머신 담당.
- **Move Engine**: 대상 창 확정, 이동 가능 여부 판정, 좌표 계산, `SetWindowPos` 호출 담당. UI 스레드와 상태를 공유하지 않고, 후크 스레드에서 동기 실행한다.
- **Tray/Config**: 트레이 아이콘, 사용/중지, 조합 키 변경, 자동 시작, 설정 파일 저장/로드. 이동 경로와 분리되어 후크 안정성에 영향을 주지 않는다.
- 프로세스는 상승 권한으로 실행한다(`requireAdministrator` 매니페스트). 관리자 권한 창 이동을 위한 전제이며, 상세는 8절을 따른다.

프로세스는 DPI 인식을 명시적으로 설정한다. 프로젝트 속성 `ApplicationHighDpiMode=PerMonitorV2`를 사용한다(매니페스트 방식은 WinForms에서 무시되므로 사용하지 않는다). 후크 좌표·`GetWindowRect`·`SetWindowPos`가 모두 물리 픽셀로 일치해야 창이 커서를 정확히 따라간다.

## 3. 입력 처리 설계

### 3.1 후크 설치

- `SetWindowsHookEx`로 `WH_MOUSE_LL` 저수준 마우스 후크를 설치한다.
- 후크 프로시저는 전용 스레드의 메시지 펌프에서 동작한다.
- 조합 키(기본값 `Win`) 상태는 후크 콜백 시점에서 `GetAsyncKeyState`로 확인한다. 필요시 `WH_KEYBOARD_LL` 병행은 후속 확장으로 둔다.

### 3.2 상태머신

```
Idle --(Win down + LButtonDown)--> Candidate
Candidate --(이동 가능 창)--> Moving
Candidate --(이동 불가 창)--> Idle (이벤트 그대로 전달)
Moving --(MouseMove)--> Moving (SetWindowPos 반복)
Moving --(LButtonUp | Esc | Win up)--> Idle
```

- `Candidate` 진입 시점에 대상 창을 확정하고 시작 좌표를 스냅샷한다.
- `Moving` 구간에서 발생한 원본 클릭·드래그 이벤트는 대상 앱으로 전달하지 않고 삼킨다. 이동이 시작되지 않은 경우(`Candidate`에서 탈락)는 원래 이벤트를 그대로 전달해 일반 클릭 동작을 보존한다.
- 시작 메뉴 억제는 `Win-down` 보류 + `Win-up` 조건부 처리로 구현한다. `Win-down`은 항상 삼키고 보류하며, 이동 세션이 있었으면 `up`도 삼킨다(셸은 `down/up`을 모두 못 봤으므로 시작 메뉴·상태 고착 없음). 이동 없이 끝나면 삼킨 `down`을 `SendInput`으로 재생한 뒤 실제 `up`을 통과시켜 시작 메뉴를 보존한다. 보류 중 다른 키가 오면(`Win+R` 등) `down`을 먼저 재생해 조합이 동작하게 한다. 합성 입력(`LLKHF_INJECTED`)은 상태 처리에서 제외해 자기 재생 루프를 방지한다.
- `Esc`, 버튼 릴리스, 조합 키 릴리스 중 하나라도 발생하면 이동을 종료하고 후크 억제를 해제한다.

## 4. 창 판정 설계

### 4.1 대상 창 확정

1. 커서 스크린 좌표에서 `WindowFromPoint`로 핸들을 얻는다.
2. 자식 컨트롤(버튼, 패널 등)일 수 있으므로 `GetAncestor(hWnd, GA_ROOT)`로 최상위 창으로 승격한다.
3. `GetWindowRect`로 창 사각형과 커서 오프셋(`cursor - rect.left/top`)을 저장한다.

### 4.2 이동 가능 여부 필터

다음 중 하나라도 해당하면 이동 모드에 진입하지 않는다:

- `IsZoomed(hWnd)`가 참인 최대화 창.
- 전체화면 창(모니터 작업 영역과 창 Rect 비교, 메뉴·테두리 없는 최상위 창).
- 작업 표시줄, 시작 메뉴 등 시스템 지정 제외 창.
- 관리자 권한 창은 제외하지 않는다. 프로세스가 상승 권한으로 실행되므로 정상 이동 대상이다. 비상승 실행(예: 수동으로 권한 없이 실행한 경우)으로 고권한 창 조작이 거부되면 무시하고, 트레이에 제한 모드 상태를 표시한다.

## 5. 이동 엔진 설계

- 입력: 이동 시작 시 `GetWindowRect` 스냅샷 + 커서 오프셋.
- 출력: 마우스 이동 델타를 더한 새 좌표 `(newX, newY)`.
- 호출: `SetWindowPos(hWnd, NULL, newX, newY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)`.
  - 크기·Z-order·활성화 상태를 바꾸지 않고 위치만 이동한다.
- 멀티모니터: 스크린 좌표계 그대로 사용한다. 모니터 경계를 클램프하지 않고, 커서가 있는 모니터로 자연스럽게 넘어가게 한다.
- DPI: 프로세스 DPI 인식을 선언해 `GetWindowRect`/`SetWindowPos` 좌표계 불일치를 방지한다. 100%/150% 혼합 환경은 검증 계획에 포함한다.

## 6. 설정·트레이 설계

- 설정 파일: `AnyMove/settings.json` (예시).
  - `modifier`: `"Win"` (기본값, `"Alt"` 등으로 변경 가능).
  - `enabled`: `true/false`.
  - `autostart`: `true/false`.
- 트레이 메뉴: 사용/중지 토글, 조합 키 변경, 자동 시작, 종료. 비상승 실행 시 제한 모드(관리자 창 미지원) 표시를 포함한다.
- 자동 시작: 작업 스케줄러 로그온 트리거(가장 높은 권한 사용)로 등록하고, 인스톨러 제거 시 함께 해제한다.
- 설정 변경은 후크 재설치 없이 다음 입력 이벤트부터 적용한다.

## 7. 예외·안정성 설계

- 후크 콜백 내 예외는 이동 모드 강제 종료 + 이베트 전달(패스스루)로 처리한다. 입력 먹통을 최우선으로 방지한다.
- `SetWindowPos` 실패 시 이동을 중단하고 일반 클릭으로 폴백한다.
- 장시간 상주 시 타이머 기반 상태 정합성 검사를 둔다(이동 모드가 비정상 잔류하면 해제).
- 킬 스위치: 트레이 중지 + 프로세스 종료만으로 시스템이 원래 상태로 복귀한다.

## 8. 빌드·배포

- 구현 언어: C#(.NET), 단일 실행 파일 게시. 인스톨러: Inno Setup. 무설치(zip)는 범위에서 제외한다.
- 앱 매니페스트에 `requireAdministrator`를 선언한다. 수동 실행 시 UAC 동의가 필요하다.
- 인스톨러는 작업 스케줄러에 로그온 태스크(가장 높은 권한)를 생성해 평소 사용 시 UAC 프롬프트 없이 시작되게 한다. 제거 시 태스크를 함께 삭제한다.
- 미서명 실행 시 Windows SmartScreen 경고가 나올 수 있음을 배포 문서에 명시한다.

## 9. 검증 계획

- 핵심 E2E: 메모장·브라우저·탐색기에서 `Win + 본문 드래그` 이동 확인, 일반 클릭 정상 동작 대조, 시작 메뉴가 뜨지 않는지 확인.
- 관리자 권한 검증: 관리자 권한 메모장 등 상승 프로세스 창 이동 확인, 비상승 실행 시 무시 및 제한 모드 표시 확인.
- 최고 위험 항목 우선: 이동 중 원본 클릭 억제 여부(텍스트 선택·버튼 오작동 여부).
- 경계: 최대화, 전체화면, 관리자 권한 앱, 멀티모니터, DPI 혼합 환경.
- 안정성: 수 시간 상주 후 CPU·메모리 증가 없음, 후크 해제 후 입력 복귀.

## 10. Sources

- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-windowfrompoint
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getancestor
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-iszoomed
- https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process
