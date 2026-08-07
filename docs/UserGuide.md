# Pay Attention — User Guide

## What it does

**Pay Attention** is a Windows system-tray app that watches the screen with Windows OCR. When text you define appears, it saves evidence (screenshot, short recording, or a sequence of stills) to a folder on your PC.

Nothing is uploaded. Captures stay local under your Windows profile.

## Install and run

1. Copy `PayAttention.exe` to a folder you like (or leave it where you unpacked it).
2. Double-click **PayAttention.exe**.
3. You do **not** need to install the .NET runtime for the published exe.

**Requirement:** Windows 10/11 with a language pack that includes **OCR** support (English or your usual display language). If monitoring fails at start, install or repair OCR language features in Windows Settings, then restart the app.

## First run

1. Look for the tray icon (near the clock). You may need to click the **^** overflow arrow.
2. Right-click the icon for the menu:
   - **Start** / **Stop** monitoring
   - **Pause** (or use the pause hotkey; default `Ctrl+Shift+P`)
   - **Settings…**
   - **Exit**
3. Open **Settings**, add at least one trigger, enable it, save, then **Start** monitoring.

## Triggers

Each trigger has:

- **Text** — the string to look for on screen  
- **Match** — `contains` (default), `exact`, or `regex`  
- **Action** — what to save when it matches  
- **Enabled** — only enabled triggers fire  

Tips:

- Prefer short, distinctive phrases over common words like “Error” unless you want frequent captures.
- `contains` is usually best. Mid-token punctuation such as `!` `@` `$` is often misread by OCR; letters, digits, and optional `#` work more reliably.

## Actions

| Action | What you get |
| --- | --- |
| **Screenshot** | One still image when the text is seen |
| **Record** | A short video of the seconds *before* the match (rolling buffer), plus a still. Default format is MJPEG `.avi` (GIF optional) |
| **Screenshot sequence** | A folder of timed stills before and after the match (for example `t-030.jpg` … `t+000.jpg` … `t+030.jpg`) |

The same trigger text will not fire again until the **debounce** period passes (default 30 seconds).

## Where files go

Default location:

`%LocalAppData%\pay_attention\`

- `config.json` — settings and triggers  
- `captures\` — screenshots, recordings, and sequence folders  

You can change the capture folder in Settings. Use **Import…** / **Export…** there to copy a full config between machines.

## Useful settings

Defaults are tuned for serial/terminal use without filling the disk too quickly:

- **OCR interval** — how often the screen is scanned (default **500 ms**). Use 250–500 ms if messages flash briefly.  
- **Debounce** — minimum time before the same trigger can fire again (default **30 s**).  
- **Monitor / scope** — primary monitor, a specific monitor, all monitors, or the foreground window. Prefer a specific monitor or foreground window when watching one terminal.  
- **Recording buffer** — pre-roll length and frame rate (defaults **15 s** at **3 FPS**, JPEG quality **60**).  
- **Sequence timing** — spacing and before/after windows (defaults **5 s** spacing, **30 s** before and after).  
- **Retention** — auto-delete old captures (defaults **14 days** or **200** items, whichever limit applies).  

OCR “quality” options (max width, upscale, invert) are advanced; the app already applies extra OCR passes for dark terminals. Captures are always saved in normal color and resolution.

## Tips for serial consoles and terminals

- Keep the interesting text **visible and uncovered** (not behind other windows).  
- Neon text on dark backgrounds (for example Consolas ~8 pt green/amber on black) works best.  
- Very dim text, tiny fonts below ~7–8 pt, or symbols jammed inside tokens are often missed.  
- If detection feels slow, lower the OCR interval and ensure the terminal is on the selected monitor.

## Privacy

This tool can capture anything visible on screen — including passwords, tokens, and personal data. Treat the captures folder as sensitive. Keep retention limits on, and delete folders you no longer need.

## Need more?

This PDF ships next to `PayAttention.exe`. For development builds and tests, see the project README in the source repository.
