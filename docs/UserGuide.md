# Pay Attention — User Guide

## What it does

**Pay Attention** watches your screen with Windows OCR. When text you define appears, it saves evidence (screenshot, short recording, or a sequence of stills) on your PC.

Nothing is uploaded. Data stays under your Windows profile.

## Install and run

1. Put `PayAttention.exe` in a folder you like (keep the PDF next to it if you have it).
2. Double-click **PayAttention.exe**.
3. You do **not** need to install the .NET runtime for this published build.

Windows may warn about an unsigned app (SmartScreen). For a private build, use **More info** → **Run anyway**.

**Requirement:** Windows 10/11 with **OCR** language support. If monitoring fails at start, install/repair OCR in Windows language settings and restart the app.

Only one copy should run at a time.

## First run

1. Find the tray icon near the clock (you may need **^**).
2. Right-click:
   - **Start** / **Stop** monitoring  
   - **Pause** / **Resume** (hotkey default `Ctrl+Shift+P`)  
   - **Settings…**  
   - **Open captures folder**  
   - **About…** (version)  
   - **Exit**
3. Open **Settings**, add a **distinctive** trigger, enable it, **Save**, then **Start** monitoring.

Avoid common words like “Error” unless you want many captures of everyday windows.

## Triggers

- **Text** — string to look for  
- **Match** — `contains` (usual), `exact`, or `regex` (invalid regex blocks Save)  
- **Action** — Screenshot, Record, or Sequence  
- **Enabled** — only enabled rows fire  

Prefer short unique phrases. Mid-token `!` `@` `$` is often misread; letters, digits, and `#` work better.

## Applications

Limit which processes OCR watches (process name, e.g. `firefox` or `PayAttention`):

- **On** — row enabled  
- **Application** — process name (**Pick…** lists running apps)  
- **List** — `Whitelist` or `Blacklist`  
- **Debounce** — overrides Capture debounce when that app is in the foreground (default **30** s)  
- **Triggers** — click the cell, then select rows in the Triggers table above (`All` if none selected). Deleted triggers are dropped from associations automatically.

**Application scope** (Capture section, default **All Minus Blacklisted Applications**):

- **All Applications** — no process filter  
- **Whitelisted Applications** — only when a whitelisted app is foreground  
- **All Minus Blacklisted Applications** — skip fully blacklisted foreground apps; blacklisted windows are masked out of OCR (so Settings text does not fire triggers)

By default **PayAttention** is an on blacklist entry so the settings UI does not capture itself.

## Actions

| Action | What you get |
| --- | --- |
| **Screenshot** | One still when the text is seen |
| **Record** | Short pre-roll video (default MJPEG `.avi`) plus a still |
| **Sequence** | Folder of timed stills before/after the match (`t-030.jpg` … `t+000.jpg` …) |

**Debounce** (default 30 s) limits how often the same trigger can fire when the screen *has* changed.

If the screen is essentially **unchanged** (console left open), it will **not** re-fire even after debounce. Mouse movement usually does not count as a new scene. New lines or a different window can allow another capture.

## Where files go

`%LocalAppData%\pay_attention\`

| Item | Purpose |
| --- | --- |
| `config.json` | Settings and triggers |
| `captures\` | Output files |
| `logs\` | App logs (can mention triggers/paths) |

Change the capture folder in Settings. **Import…** / **Export…** copy settings between PCs; the capture path is not forced from the other machine (missing paths fall back to this PC’s default).

## Useful settings

- **OCR interval** — scan rate (default **500 ms**; try 250–500 for brief messages)  
- **Debounce** — min gap after a *changed* scene (default **30 s**; per-app debounce can override)  
- **Application scope** — all / whitelisted / all minus blacklisted (default **all minus blacklisted**)  
- **Monitor / capture scope** — primary, specific, all, or foreground window  
- **Recording** — buffer length / FPS / quality (defaults **15 s**, **3 FPS**, JPEG **60**)  
- **Sequence** — spacing / before / after (defaults **5 / 30 / 30** s)  
- **Start with Windows** / **Start monitoring on launch**  
- **Retention** — auto-delete (defaults **14 days** or **200** items)  
- **Debug JSON sidecars** — leave **off** (can store OCR text next to captures)  

OCR max width / upscale / invert are advanced; extra OCR passes for dark terminals already run. Saved captures stay full color.

## Tips for serial consoles

- Keep the text **visible and uncovered**  
- Neon Consolas ~8 pt on black works well  
- Very dim text, tiny fonts, or odd punctuation may be missed  
- Match the monitor / foreground scope to the terminal  

## Privacy

Can capture passwords and secrets. Treat `captures\` and `logs\` as sensitive. Keep retention on. Delete data you no longer need.

## Stop / remove

1. Tray → **Exit**  
2. Uncheck **Start with Windows** in Settings (or remove the `pay_attention` Run key)  
3. Delete the exe folder and optionally `%LocalAppData%\pay_attention\`  
