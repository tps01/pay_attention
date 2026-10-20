"""A rendered scene change is suppressed until the completed event's debounce expires."""

from __future__ import annotations

from datetime import datetime, timezone
import json
import os
import time

import colosseum as col
from support_captures import (
    SHOT_TRIGGER,
    appdata,
    completed_capture,
    log_size,
    make_appdata,
    release_desktop,
    require_live_execution,
    seed_screenshot_config,
    snapshot_captures,
    start_monitor_process,
    verify_no_capture,
    wait_for_new_capture,
    wait_for_status,
    write_gui_config,
)


def _completed_at(path):
    receipt = json.loads(path.with_suffix(".capture.json").read_text(encoding="utf-8"))
    return datetime.fromisoformat(receipt["completedAt"])


def main() -> None:
    require_live_execution()
    if "PAY_ATTENTION_APPDATA" not in os.environ:
        make_appdata()
        os.environ["PAY_ATTENTION_APPDATA"] = str(appdata())
    debounce = 12
    seed_screenshot_config(trigger_text=SHOT_TRIGGER, debounce_seconds=debounce)
    control = appdata() / "debounce-scene.json"
    gui = appdata() / "gui.toml"
    write_gui_config(
        gui,
        fixture_title="PaDebounce",
        fixture_args=(
            f"fixture --phrase {SHOT_TRIGGER} --title PaDebounce "
            f'--x 40 --y 40 --w 420 --h 320 --easy --control "{control.as_posix()}"'
        ),
    )
    col.config.load_config(str(gui), force=True)
    try:
        col.gui.desktop.connect(desktop_id=2)
        col.gui.desktop.wait_stable(desktop_id=2, timeout_s=5)
        before = snapshot_captures()
        start_monitor_process()
        # Defer pixel proof so its OCR cannot consume the debounce interval.
        first = wait_for_new_capture(before, trigger_text=SHOT_TRIGGER, prove_pixels=False)
        if first is None:
            raise AssertionError("Initial completed capture did not appear")
        first_completed = _completed_at(first)
        after_first = snapshot_captures()
        log_before = log_size()
        temporary = control.with_suffix(".tmp")
        temporary.write_text(json.dumps({"scene": 1, "x": 240}), encoding="utf-8")
        temporary.replace(control)
        deadline = time.monotonic() + 3
        ack = control.with_suffix(control.suffix + ".ack")
        while not ack.exists() or ack.read_text(encoding="utf-8") != "1":
            if time.monotonic() >= deadline:
                raise AssertionError("Fixture did not acknowledge the rendered scene change")
            time.sleep(0.05)
        wait_for_status(f"Trigger '{SHOT_TRIGGER}' suppressed (debounce", after=log_before)
        if (datetime.now(timezone.utc) - first_completed).total_seconds() > debounce - 3:
            raise AssertionError("Insufficient debounce interval remained after scene acknowledgement")
        verify_no_capture(key="changed_scene_inside_debounce", before=after_first, settle_s=2)
        # Watch the same changed scene through expiry; do not race an automatic resample.
        second = wait_for_new_capture(
            after_first, trigger_text=SHOT_TRIGGER, timeout_s=debounce + 15, prove_pixels=False
        )
        if second is None:
            raise AssertionError("Changed scene did not fire after debounce expiry")
        elapsed = (_completed_at(second) - first_completed).total_seconds()
        if elapsed < debounce:
            raise AssertionError(f"Same trigger fired before debounce expired: {elapsed:.3f}s")
        for path in (first, second):
            if not completed_capture(path, trigger_text=SHOT_TRIGGER):
                raise AssertionError(f"Capture proof failed: {path}")
    finally:
        release_desktop()


if __name__ == "__main__":
    main()
    col.endex()
