# AnyMove

[![GitHub release](https://img.shields.io/github/v/release/wolfdate25/AnyMove)](https://github.com/wolfdate25/AnyMove/releases) [![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE) [![Platform](https://img.shields.io/badge/platform-Windows-0078D6)](https://github.com/wolfdate25/AnyMove/releases)

[English](README.md) · [简体中文](README.zh-CN.md)

윈도우에서 타이틀바가 좁거나 잡기 불편한 창도, 조합 키 + 마우스 드래그만으로
화면상 어느 위치를 눌러도 창 전체를 이동시키는 상주형 유틸리티.

![icon](docs/icon.png)

## 사용법

- `Win + 좌버튼 드래그`: 커서 아래 창의 어느 영역을 눌러도 창 전체가 따라 이동
- 조합 키는 트레이 메뉴에서 `Win` / `Alt`로 변경 가능
- `Esc` 또는 버튼 릴리스로 이동 종료
- 일반 클릭(조합 키 없음)은 기존 동작 그대로 유지

## 설치

`dist` 산출물이 아닌 [Releases](../../releases)의 `AnyMove-Setup-<버전>.exe`로 설치.
인스톨러가 작업 스케줄러 로그온 태스크(가장 높은 권한)를 등록해
UAC 프롬프트 없이 관리자 권한으로 자동 시작한다.

## 직접 빌드

필요 도구: .NET 8 SDK, Inno Setup 6 (`ISCC`가 PATH에 있어야 함)

```powershell
# 저장소 루트에서 실행
dotnet publish src/AnyMove/AnyMove.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o dist/app
ISCC installer/anymove.iss
```

자세한 방법은 [BUILD.md](BUILD.md), 기술 설계는 [docs/design.md](docs/design.md),
개발 계획은 [plan.md](plan.md) 참고.

## 주의

- 관리자 권한 창 이동을 위해 `requireAdministrator`로 실행되므로 실행 시 UAC 동의가 필요하다.
- 코드 서명이 없어 첫 실행 시 SmartScreen 경고가 나올 수 있다.
- 전역 입력 후크를 사용하므로 백신·보안 SW에서 오진할 수 있다.

## 라이선스

MIT — [LICENSE](LICENSE)
