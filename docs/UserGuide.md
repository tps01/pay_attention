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
   - **Settings…**  
   - **Open captures folder**  
   - **Open logs folder**  
   - **About…** (version)  
   - **Exit**
3. Open **Settings**, add a **distinctive** trigger, enable it, **Save**, then **Start** monitoring.

Avoid common words like “Error” unless you want many captures of everyday windows.

## Triggers

The Triggers and Applications sections in Settings can be collapsed (click the section header) so long lists do not crowd the rest of the page. When expanded, each table scrolls inside a capped height.

- **Text** — string to look for  
- **Match** — `contains` (usual), `exact`, or `regex` (invalid regex blocks Save)  
- **Action** — Screenshot, Record, or Sequence  
- **OCR** — `Thorough` (default) or `Fast` (see below)  
- **Enabled** — only enabled rows fire  

### Choosing Thorough vs Fast

| Mode | Use when | Trade-off |
| --- | --- | --- |
| **Thorough** (default) | Text stays on screen (logs, dialogs, dashboards) or is small / low-contrast / oddly punctuated | Runs more OCR passes (slower per frame, higher chance of a match) |
| **Fast** | Text appears only briefly (fast-scrolling consoles, kernel dumps, flashing status lines) | Prefers fresh screen samples over exhausting every OCR fallback on one frame |

Use **Fast** for lines you expect to scroll away in about a second. Use **Thorough** for hard-to-read tokens that stay visible. You can mix modes across triggers: while only **Fast** triggers remain unmatched, OCR spends less time on fallbacks so fresh frames win; **Thorough** triggers keep the deeper multi-pass search. Each matching row still fires, including two rows that share a phrase but use different actions (Screenshot and Record). A hit is saved as soon as its pass matches, without waiting for later passes that are still looking for other triggers. After a finished search that already matched something, a static scene stays quiet briefly instead of re-scanning every grab — but if any enabled trigger is still unmatched, the same scene is OCR’d again about every **10 seconds** so sticky text is not ignored forever. A total miss is tried again immediately.

Pair **Fast** with a shorter **OCR interval** (about 250–500 ms) when catching flash-by text.

**Fast match threshold** (Capture settings, default **90%**) controls how closely OCR text must resemble a **Fast** trigger phrase. This is a similarity score against your trigger text—not a Windows OCR confidence value. **100%** is strictest (same idea as today’s matching); lower values are more reactive on garbled OCR but raise false positives. **Thorough** triggers ignore this slider.

### Writing triggers that OCR can read

Windows OCR is good at **readable words and short phrases**, not random keyboard mash.

- Prefer a distinctive **chunk of real words** or a known message fragment, e.g. `RIP INEXACT`, `Machine check`, `PAYATTN_EXAMPLE`.  
- Avoid long strings of random characters like `asadf788ynf` or mixed punctuation soup — OCR often misreads them.  
- Mid-token `!` `@` `$` is frequently dropped or altered; letters, digits, spaces, `_`, and `#` are more reliable.  
- Prefer **contains** match on a stable substring rather than an exact match on a whole noisy line.  
- Keep the text **visible, uncovered, and reasonably sized**; neon Consolas on black works well for serial consoles.  
- Avoid everyday words like “Error” unless you want many everyday captures.

## Applications

Limit which processes OCR watches (process name, e.g. `firefox` or `PayAttention`):

- **On** — row enabled  
- **Application** — process name (**Pick…** lists running apps, or **Browse…** to an `.exe`)  
- **List** — `Whitelist` or `Blacklist`  
- **Debounce** — overrides Capture debounce when that app is in the foreground (default **30** s)  
- **Triggers** — dropdown: **All**, **Selected** (opens a multi-select picker), or **None**. Deleted triggers are dropped from associations automatically.

Pause monitoring with the hotkey (default `Ctrl+Shift+P`) in Settings → Behavior.

**Application scope** (Capture section, default **All Minus Blacklisted Applications**):

- **All Applications** — no process filter  
- **Whitelisted Applications** — OCR captures **only** whitelisted app windows (best when you want to ignore everything else, even on other monitors)  
- **All Minus Blacklisted Applications** — skip OCR when a *fully* blacklisted app is in the **foreground**; otherwise blacklisted windows are masked out of the grab (so Settings text does not fire). Prefer Whitelist if a blacklisted browser on a side monitor must never be read.

By default **PayAttention** is an on blacklist entry so the settings UI does not capture itself.

**Monitor** (when capture scope is Monitor): prefer **Primary** or **Specific**. **All** scans every display and is slower—especially with Fast OCR on multi-monitor setups.

**Capture scope**: **Monitor** watches the chosen display(s). **Foreground window** only OCRs the focused app and needs that window to stay focused.

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
| `logs\` | App logs (can mention triggers/paths; auto-deleted after **5 days**) |

Change the capture folder in Settings. **Import…** / **Export…** copy settings between PCs; the capture path is not forced from the other machine (missing paths fall back to this PC’s default).

## Useful settings

- **OCR interval** — capture period between grabs (default **500 ms**; try 250–500 with **Fast** OCR for brief messages)  
- **Fast match threshold** — similarity for **Fast** triggers only (default **90%**; lower = looser)  
- **Debounce** — min gap after a *changed* scene (default **30 s**; per-app debounce can override)  
- **Application scope** — all / whitelisted / all minus blacklisted (default **all minus blacklisted**)  
- **Monitor / capture scope** — primary, specific, all, or foreground window  
- **Recording** — buffer length / FPS / quality (defaults **15 s**, **3 FPS**, JPEG **60**)  
- **Sequence** — spacing / before / after (defaults **5 / 30 / 30** s)  
- **Start with Windows** / **Start monitoring on launch**  
- **Log level** — `Info` (default) or `Debug` (OCR snippets, enqueue/fingerprint, resample, fire-gate reasons in `logs\`)  
- **Retention** — auto-delete captures (defaults **14 days** or **200** items); app logs older than **5 days** are always pruned  
- **Debug JSON sidecars** — leave **off** (can store OCR text next to captures)  

### Submitting logs with a bug report

1. Settings → Behavior → **Log level** = **Debug**, Save  
2. Reproduce the miss (leave sticky text on screen long enough for a ~10 s re-OCR if needed)  
3. Tray → **Open logs folder**  
4. Attach the relevant `app-yyyyMMdd.log` file(s) (treat as sensitive — they can include OCR text)

OCR max width / upscale / invert are advanced; extra OCR passes for dark terminals already run. Saved captures stay full color.

## Tips for serial consoles

- Keep the text **visible and uncovered**  
- Neon Consolas ~8 pt on black works well  
- Very dim text, tiny fonts, or odd punctuation may be missed  
- Match the monitor / foreground scope to the terminal  

## Privacy

Can capture passwords and secrets. Treat `captures\` and `logs\` as sensitive. Keep capture retention on. App logs older than 5 days are deleted automatically. Delete data you no longer need.

## Stop / remove

1. Tray → **Exit**  
2. Uncheck **Start with Windows** in Settings (or remove the `pay_attention` Run key)  
3. Delete the exe folder and optionally `%LocalAppData%\pay_attention\`  
