"""A changed scene cannot re-fire the same trigger until debounce expires."""

from __future__ import annotations

import os
import time
import colosseum as col
from support_captures import (
    SHOT_TRIGGER,
    appdata,
    make_appdata,
    seed_screenshot_config,
    snapshot_captures,
    start_monitor_process,
    stop_tracked_processes,
    verify_capture_appeared,
    verify_no_capture,
    write_gui_config,
    require_live_execution,
)


def main() -> None:
    require_live_execution()
    if "PAY_ATTENTION_APPDATA" not in os.environ:
        make_appdata()
        os.environ["PAY_ATTENTION_APPDATA"] = str(appdata())
    seed_screenshot_config(trigger_text=SHOT_TRIGGER, debounce_seconds=12)
    gui = appdata() / "gui.toml"
    from colosseum_gui.connections import close_all

    def scene(number: int) -> None:
        close_all()
        title = f"PaDebounce{number}"
        write_gui_config(
            gui,
            fixture_title=title,
            fixture_args=(
                f"fixture --phrase {SHOT_TRIGGER} --title {title} "
                f"--x {40 + number * 200} --y 40 --w 420 --h 320 --easy"
            ),
        )
        col.config.load_config(str(gui), force=True)
        col.gui.desktop.connect(desktop_id=2)
        col.gui.desktop.wait_stable(desktop_id=2, timeout_s=5)

    scene(0)
    before = snapshot_captures()
    first_time = time.monotonic()
    start_monitor_process()
    verify_capture_appeared(key="first_shot", before=before, trigger_text=SHOT_TRIGGER)
    latest_first_time = time.monotonic()
    after_first = snapshot_captures()
    scene(1)  # Different pixels, same process session, trigger ID and text.
    if time.monotonic() - first_time >= 8:
        raise AssertionError(
            "Fixture replacement was too slow to prove debounce suppression"
        )
    verify_no_capture(
        key="changed_scene_inside_debounce", before=after_first, settle_s=2
    )
    time.sleep(max(0, 13 - (time.monotonic() - latest_first_time)))
    before_expired = snapshot_captures()
    scene(2)  # New scene after expiry must fire that same trigger again.
    verify_capture_appeared(
        key="same_trigger_after_expiry",
        before=before_expired,
        trigger_text=SHOT_TRIGGER,
    )
    close_all()
    stop_tracked_processes()


if __name__ == "__main__":
    main()
    col.endex()
