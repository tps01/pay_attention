# pay_attention

Windows tray app that watches the screen with Windows OCR and saves a screenshot, recording, or still sequence when text you care about appears.

Useful for taking screenshots automatically, so you don't have to. Also useful for debugging failures in post, when someone else was working through something.

Local only, with no cloud support.

## Share with someone

From a machine with the .NET 8 SDK:

```powershell
.\scripts\publish.ps1
```

Give them the contents of `publish\`:

- `PayAttention.exe`
- `PayAttention-UserGuide.pdf` (if the PDF build succeeded)

**Notes for recipients**

- Windows may show SmartScreen for an unsigned exe — More info → Run anyway is normal for a private build.
- No .NET install is required for the published exe.
- Windows OCR language support is required (English or your UI language).
- One instance at a time (second launch shows a notice).

**Uninstall / clean up**

1. Tray icon → Exit
2. In Settings, uncheck Start with Windows (or delete the `pay_attention` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`)
3. Delete the exe folder and, if desired, `%LocalAppData%\pay_attention\` (config, captures, logs)

## Features

- Tray start/stop/pause, settings, pause hotkey (default `Ctrl+Shift+P`)
- Triggers: `contains` / `exact` / `regex`
- Actions: Screenshot, Record (pre-roll AVI/GIF + still), Screenshot sequence
- Debounce + unchanged-screen suppression (sticky consoles do not spam captures)
- Import/Export settings JSON; multi-monitor / foreground-window scope; retention

## Requirements

- Windows 10/11 with Windows OCR language support
- Published exe: no separate .NET install (~50–100MB+)

## Build

```powershell
.\scripts\publish.ps1
```

Or `scripts\publish.bat` if script execution is blocked. Output: `publish\PayAttention.exe` and usually `publish\PayAttention-UserGuide.pdf`.

## Development

```powershell
dotnet build PayAttention.sln -c Release
dotnet run --project src/PayAttention -c Release
```

## Config and data

Under `%LocalAppData%\pay_attention\`:

| Path | Purpose |
| --- | --- |
| `config.json` | Settings and triggers |
| `captures\` | Screenshots, recordings, sequence folders |
| `logs\` | App log files (may include trigger names/paths) |

Defaults (new installs): OCR every **500 ms**, debounce **30 s**, record buffer **15 s @ 3 FPS** JPEG **60**, sequence **5 / 30 / 30** s, retention **14 days / 200** items. Monitoring does **not** start until you start it (unless you enable start-on-launch).

Import/Export copies triggers and settings. Capture folder in exports is left blank so the other PC uses its own default.

### OCR limits

Works best with Consolas ~8pt+ neon-on-black (serial/terminal style). Mixed alnum triggers; optional `#` is OK.

Unreliable: very dim text, mid-token `!` `@` `$`, covered windows, fonts well below ~7–8pt.

## Privacy

Can capture anything on screen, including secrets. Treat `captures\` and `logs\` as sensitive. Keep retention on. Leave **Debug: save JSON sidecar** off unless troubleshooting (sidecars can store OCR text).

## Tests

```powershell
pip install -r tests/requirements.txt
pytest tests -q
```

On-screen E2E (interactive session; keep harness uncovered):

```powershell
pytest tests/test_screen_e2e.py tests/test_screen_e2e_stream.py -q
```
