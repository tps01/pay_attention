# Colosseum suite for pay_attention

Windows-only interactive suite. Mixes FlaUI (`colosseum-gui`) against `PayAttention.exe`
with harness-driven on-screen OCR cases. Capture proofs require a **completed event
receipt, matching trigger identity, decoded media and production OCR of the saved
still** under an isolated app-data folder. Harness `PASS` follows that proof.

## Prerequisites

- Windows 10/11 with a desktop session (fixture / harness windows must stay uncovered)
- .NET 8 SDK
- Python 3.12+ with a venv
- Product hooks: `PAY_ATTENTION_APPDATA`, `--settings`, `--start`
- **colosseum-gui 0.3.1+** (`args=` as a string or list, chord `press_key` such as `ctrl+shift+p`)
- **colosseum-core 0.16.6+** (suite script directory on `sys.path`; `load_config(..., force=True)` replaces the pinned `-g` file)

## Install Colosseum from GitHub Releases

Wheels ship on each plugin’s GitHub Releases page (not only PyPI). For this suite you need **core** and **gui**:

- [tps01/colosseum-core releases](https://github.com/tps01/colosseum-core/releases)
- [tps01/colosseum-gui releases](https://github.com/tps01/colosseum-gui/releases)

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1

pip install `
  https://github.com/tps01/colosseum-core/releases/download/v0.16.6/colosseum_core-0.16.6-py3-none-any.whl `
  https://github.com/tps01/colosseum-gui/releases/download/v0.3.1/colosseum_gui-0.3.1-py3-none-any.whl
```

The local pollers additionally validate product receipts and saved pixels; a generic file-appearance check does not replace that proof.

Offline zips on the release pages bundle deps (`colosseum-*-offline-windows-py3.12.zip` → `pip install --no-index --find-links=wheelhouse colosseum-gui`).

Do **not** pin published **gui v0.1.10** — it lacks launch `args=` and chord keys.

## Run

```powershell
# From the pay_attention repo, with the venv activated:
colosseum run-suite tests/colosseum/suite.toml -g tests/colosseum/config.toml
```

Suite setup builds Release binaries, creates a temp app-data root, and writes an absolute-path `gui.toml` under that root.

## Cases

| Script | What it proves |
| --- | --- |
| `test_build_smoke.py` | Solution Release build + xunit `PayAttention.Tests` |
| `test_settings.py` | Edit debounce 30 → 17; Save, read config and verify reloaded field |
| `test_launch_args_list.py` | FlaUI `args` list with a title that contains spaces |
| `test_screenshot.py` | Fixture phrase → `*_screenshot.jpg` |
| `test_debounce.py` | Changed scene suppressed inside debounce; same trigger fires after expiry |
| `test_pause_hotkey.py` | `Ctrl+Shift+P` pauses then resumes |
| `test_scope.py` | Whitelist Selected captures Alpha; decoy process does not |
| `test_pairwise.py` | 10-case pairwise matrix (difficulty × scroll × OCR × scope) |
| `test_many.py` | 50 triggers; real phrase at first / mid / last slot |
| `test_dmesg.py` | Fast dmesg/MCE stream → screenshot + recording within latency budget |

Matcher / config unit coverage stays in `tests/PayAttention.Tests` (xunit).

## Execution and evidence

Use `full` mode (the default). `procedure` runs live actions without verification; it is not an acceptance result. These scripts reject `verify_only` before any build, process launch or config mutation; offline replay of desktop scenarios is not supported.

Setup caches a single solution build for the suite process. Standalone helpers build once too. Teardown archives the isolated config, media and app logs before deleting its temporary app-data root. Archives and harness stdout/stderr are under the Colosseum output directory when available, otherwise `artifacts/colosseum/evidence-<id>`. Harnesses retain their own temporary workspaces as an additional recovery source, including on failure. The printed original capture paths therefore remain inspectable. Timeout handling also recovers workspaces printed by killed harnesses. Evidence contains fixture data and may include desktop pixels from monitor-scope cases; review it before sharing.

The harness `inspect <capture-path> <config-path>` command performs offline completion/media/content verification without capturing the screen. Video validation decodes AVI frames (or GIF frames) and OCR checks its paired trigger still; this does not prove every video frame contains the trigger.

Offline helper regressions (requires `pytest` and Pillow in addition to core):

```powershell
python -m pytest tests/python -q
```

## Verbose diagnostics

The suite seeds `logLevel: "verbose"` for the app and sets
`PAY_ATTENTION_HARNESS_VERBOSE=1` for harness runs. For a standalone harness run:

```powershell
$env:PAY_ATTENTION_HARNESS_VERBOSE = "1"
tests/PayAttention.Harness/bin/Release/net8.0-windows10.0.19041.0/PayAttention.Harness.exe pairwise 2 45
```

Verbose includes Debug logging plus frame timestamp/epoch/fingerprint, queue age,
backlog and dropped-frame counts, mask counts, crop coordinates, per-pass image
sizes/preprocessing/timing/text/matched trigger IDs, and budget cutoffs. Compare
the same frame identifier across lines to distinguish queue delay, unreadable
pixels, scope filtering, and incomplete searches. Logs and captured screen content
can contain sensitive information; review evidence before sharing it.

Completion receipts now include capture-time window/monitor rectangles, screen
origin, OCR settings/prep overrides, and completion time. `inspect` replays those
rectangles against saved pixels without enumerating today's desktop. Older
receipts use an image-only fallback. Moving or closing fixtures after capture
does not change the recorded crop geometry.

Window OCR gives each candidate a baseline before bounded deeper searches. Small
terminal panes also receive line crops and an alternative crop excluding the final
isolated glyph; this recovers rows that Windows OCR rejects because of a trailing
delimiter. Saved pixels remain unchanged. Harness overlays reassert native topmost
Z-order and report their center-point window owner when visibility fails.

The debounce case changes one fixture in place, waits for its scene acknowledgement
and a logged suppression, and measures completed-event times before running pixel
proof. Pairwise testing attempts all ten cases, records a separate verification for
each, and reports accumulated failures.
