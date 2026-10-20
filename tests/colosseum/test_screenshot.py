"""On-screen fixture + real PayAttention.exe → capture file appears."""

from __future__ import annotations

import os
import time

import colosseum as col

from support_captures import (
    SHOT_TRIGGER,
    appdata,
    load_gui,
    make_appdata,
    seed_screenshot_config,
    release_desktop,
    snapshot_captures,
    start_monitor_process,
    verify_capture_appeared,
)


def main() -> None:
    from support_captures import require_live_execution

    require_live_execution()
    if "PAY_ATTENTION_APPDATA" not in os.environ:
        make_appdata()
        os.environ["PAY_ATTENTION_APPDATA"] = str(appdata())
        load_gui()

    seed_screenshot_config(
        trigger_text=SHOT_TRIGGER,
        debounce_seconds=60,
        enabled=True,
    )

    try:
        col.gui.desktop.connect(desktop_id=2)
        col.gui.desktop.wait_stable(desktop_id=2, timeout_s=5.0)
        before = snapshot_captures()
        start_monitor_process()
        time.sleep(1.0)

        verify_capture_appeared(key="shot_file", before=before, timeout_s=15.0)
        col.gui.desktop.capture_screenshot(desktop_id=2, path="captures/fixture_shot.png")
    finally:
        release_desktop()


if __name__ == "__main__":
    main()
    col.endex()
