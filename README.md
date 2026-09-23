# AnyMove

[한국어](README.ko.md) · [简体中文](README.zh-CN.md)

A Windows tray utility that moves any window by dragging it from anywhere
with a modifier key + mouse drag, even windows with narrow or
hard-to-grab title bars.

![icon](docs/icon.png)

## Usage

- `Win + Left-drag`: the whole top-level window follows, no matter which
  part of it (body, buttons, etc.) you grabbed
- Modifier key can be switched between `Win` / `Alt` in the tray menu
- `Esc` or button release ends the move
- Plain clicks (no modifier) behave exactly as before

## Install

Install `AnyMove-Setup-<version>.exe` from [Releases](../../releases).
The installer registers a logon task in Task Scheduler (highest privileges),
so AnyMove auto-starts elevated without a UAC prompt.

## Build from source

Requires: .NET 8 SDK, Inno Setup 6 (`ISCC` on PATH)

```powershell
# run from the repository root
dotnet publish src/AnyMove/AnyMove.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o dist/app
ISCC installer/anymove.iss
```

See [BUILD.md](BUILD.md) for details, [docs/design.md](docs/design.md) for the
technical design, and [plan.md](plan.md) for the development plan.

## Notes

- Runs as `requireAdministrator` to move elevated windows, so launching it
  asks for UAC consent.
- Not code-signed: Windows SmartScreen may warn on first run.
- Uses global input hooks, so antivirus/security software may flag it.

## License

MIT — [LICENSE](LICENSE)
