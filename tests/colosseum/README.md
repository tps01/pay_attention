# Colosseum suite for pay_attention

Windows-only interactive suite. Mixes FlaUI (`colosseum-gui`) against `PayAttention.exe`
with harness-driven on-screen OCR cases. Capture proofs use **new files under an
isolated app-data folder** or harness stdout (`PASS`, `LATENCY`, …), not UIA OCR text.

## Prerequisites

- Windows 10/11 with a desktop session (fixture / harness windows must stay uncovered)
- .NET 8 SDK
- Python 3.9+ with a venv
- Product hooks: `PAY_ATTENTION_APPDATA`, `--settings`, `--start`
- **colosseum-gui 0.2.3+** (`args=` on `[[gui.desktop]]`, chord `press_key` such as `ctrl+shift+p`)
- **colosseum-core 0.16.2+** (suite script directory on `sys.path` for sibling helpers)

## Install Colosseum from GitHub Releases

Wheels ship on each plugin’s GitHub Releases page (not only PyPI). For this suite you need **core** and **gui**:

- [tps01/colosseum-core releases](https://github.com/tps01/colosseum-core/releases)
- [tps01/colosseum-gui releases](https://github.com/tps01/colosseum-gui/releases)

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1

pip install `
  https://github.com/tps01/colosseum-core/releases/download/v0.16.2/colosseum_core-0.16.2-py3-none-any.whl `
  https://github.com/tps01/colosseum-gui/releases/download/v0.2.3/colosseum_gui-0.2.3-py3-none-any.whl
```

Optional: [colosseum-shared 0.3.5+](https://github.com/tps01/colosseum-shared/releases/tag/v0.3.5) provides `verify_new_file` / `verify_no_new_file` if you want to replace the local capture pollers in `support_captures.py`.

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
| `test_settings.py` | Settings window Save + config.json round-trip |
| `test_screenshot.py` | Fixture phrase → `*_screenshot.jpg` |
| `test_debounce.py` | No second capture during debounce; new phrase fires |
| `test_pause_hotkey.py` | `Ctrl+Shift+P` pauses then resumes |
| `test_scope.py` | Whitelist Selected captures Alpha; decoy process does not |
| `test_pairwise.py` | 8-case pairwise matrix (difficulty × scroll × OCR × scope) |
| `test_many.py` | 50 triggers; real phrase at first / mid / last slot |
| `test_dmesg.py` | Fast dmesg/MCE stream → screenshot + recording within latency budget |

Matcher / config unit coverage stays in `tests/PayAttention.Tests` (xunit).
