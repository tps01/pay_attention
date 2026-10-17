"""Pause hotkey (Ctrl+Shift+P) stops captures until pressed again."""

from __future__ import annotations

import os
import time
import uuid

import colosseum as col

from support_captures import (
    appdata,
    make_appdata,
    seed_screenshot_config,
    snapshot_captures,
    start_monitor_process,
    stop_tracked_processes,
    verify_capture_appeared,
    verify_no_capture,
    write_gui_config,
)

PAUSE_A = "pA7nQ1wX"
PAUSE_B = "pB8mR2yZ"


def main() -> None:
    if "PAY_ATTENTION_APPDATA" not in os.environ:
        make_appdata()
        os.environ["PAY_ATTENTION_APPDATA"] = str(appdata())

    seed_screenshot_config(trigger_text=PAUSE_A, debounce_seconds=5, enabled=True)
    import json

    cfg_path = appdata() / "config.json"
    cfg = json.loads(cfg_path.read_text(encoding="utf-8"))
    cfg["triggers"].append(
        {
            "id": str(uuid.uuid4()),
            "text": PAUSE_B,
            "match": "contains",
            "action": "screenshot",
            "ocrMode": "thorough",
            "enabled": True,
        }
    )
    cfg_path.write_text(json.dumps(cfg, indent=2), encoding="utf-8")

    gui = appdata() / "gui.toml"
    write_gui_config(
        gui,
        fixture_title="PaPauseA",
        fixture_args=(
            f"fixture --phrase {PAUSE_A} --title PaPauseA "
            f"--x 40 --y 40 --w 420 --h 320 --easy"
        ),
    )
    col.config.load_config(str(gui), force=True)

    col.gui.desktop.connect(desktop_id=2)
    col.gui.desktop.wait_stable(desktop_id=2, timeout_s=5.0)
    before = snapshot_captures()
    start_monitor_process()
    verify_capture_appeared(key="before_pause", before=before, timeout_s=15.0)

    # One chord only — MOD_NOREPEAT must keep this from double-toggling.
    col.gui.desktop.press_key(desktop_id=2, key="ctrl+shift+p")
    time.sleep(0.5)

    from colosseum_gui.connections import close_all

    close_all()
    write_gui_config(
        gui,
        fixture_title="PaPauseB",
        fixture_args=(
            f"fixture --phrase {PAUSE_B} --title PaPauseB "
            f"--x 40 --y 40 --w 420 --h 320 --easy"
        ),
    )
    col.config.load_config(str(gui), force=True)
    col.gui.desktop.connect(desktop_id=2)
    col.gui.desktop.wait_stable(desktop_id=2, timeout_s=5.0)
    mid = snapshot_captures()
    verify_no_capture(key="paused_quiet", before=mid, settle_s=8.0)

    col.gui.desktop.press_key(desktop_id=2, key="ctrl+shift+p")
    time.sleep(0.5)
    after_resume = snapshot_captures()
    verify_capture_appeared(key="after_resume", before=after_resume, timeout_s=15.0)

    close_all()
    stop_tracked_processes()


if __name__ == "__main__":
    main()
    col.endex()
