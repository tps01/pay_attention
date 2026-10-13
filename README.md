# pay_attention

Windows tray app that watches the screen with Windows OCR and saves a screenshot, recording, or still sequence when text you care about appears.

Useful for taking screenshots automatically, so you don't have to. Also useful for debugging failures in post, when someone else was working through something.

Local only, with no cloud support.

## Share with someone

Prefer a **[GitHub Release](https://github.com/tps01/pay_attention/releases)** zip (exe + user guide). To publish one after pushing code:

```powershell
git tag v1.4.2
git push origin v1.4.2
```

That runs CI, builds the win-x64 package, and attaches `PayAttention-1.4.2-win-x64.zip` to the release.

Or build locally with the .NET 8 SDK and hand them `publish\`:

```powershell
.\scripts\publish.ps1
```

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
- Static unmatched scenes re-OCR about every 60s so sticky text is not ignored forever
- Import/Export settings JSON; multi-monitor / foreground-window scope; capture retention; 5-day log pruning

## Requirements

- Windows 10/11 with Windows OCR language support
- Published exe: no separate .NET install (~50–100MB+)

## Build

```powershell
.\scripts\publish.ps1
```

Optional version for About / assembly info: `.\scripts\publish.ps1 -Version 1.4.2`

Or `scripts\publish.bat` if script execution is blocked. Output: `publish\PayAttention.exe` and usually `publish\PayAttention-UserGuide.pdf`.

### Release

Tag and push to create a GitHub Release with a zip (see Share above).

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
| `logs\` | App log files (may include trigger names/OCR text; pruned after **5 days**) |

Defaults (new installs): OCR every **500 ms**, debounce **30 s**, record buffer **15 s @ 3 FPS** JPEG **60**, sequence **5 / 30 / 30** s, capture retention **14 days / 200** items. Monitoring does **not** start until you start it (unless you enable start-on-launch).

Import/Export copies triggers and settings. Capture folder in exports is left blank so the other PC uses its own default.

### OCR limits

Works best with Consolas ~8pt+ neon-on-black (serial/terminal style). Mixed alnum triggers; optional `#` is OK.

Unreliable: very dim text, mid-token `!` `@` `$`, covered windows, fonts well below ~7–8pt.

## Privacy

Can capture anything on screen, including secrets. Treat `captures\` and `logs\` as sensitive. Keep capture retention on; app logs older than 5 days are pruned. For bug reports, set **Log level** to **Debug**, reproduce, then tray → **Open logs folder**. Leave **Debug: save JSON sidecar** off unless troubleshooting (sidecars can store OCR text).

## Tests

```powershell
pip install -r tests/requirements.txt
pytest tests -q
```

On-screen E2E (interactive session; keep harness uncovered). Pairwise matrix covers
difficulty × scroll × speed × multi-pane × OCR × whitelist/blacklist (8 cases). Optional
`test_screen_e2e_many.py` still stresses 50-trigger quiet behavior.

```powershell
pytest tests/test_screen_e2e_pairwise.py tests/test_screen_e2e_many.py -q
```
