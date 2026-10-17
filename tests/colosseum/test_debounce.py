"""Debounce: same scene does not re-fire; a new phrase does."""

from __future__ import annotations

import json
import os

import colosseum as col

from support_captures import (
    DEBOUNCE_TRIGGER,
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
)


def main() -> None:
    if "PAY_ATTENTION_APPDATA" not in os.environ:
        make_appdata()
        os.environ["PAY_ATTENTION_APPDATA"] = str(appdata())

    seed_screenshot_config(
        trigger_text=SHOT_TRIGGER,
        debounce_seconds=45,
        enabled=True,
    )
    # Second trigger for the refreshed phrase.
    cfg_path = appdata() / "config.json"
    cfg = json.loads(cfg_path.read_text(encoding="utf-8"))
    cfg["triggers"].append(
        {
            "id": "00000000-0000-0000-0000-00000000deb1",
            "text": DEBOUNCE_TRIGGER,
            "match": "contains",
            "action": "screenshot",
            "ocrMode": "thorough",
            "enabled": True,
        }
    )
    cfg_path.write_text(json.dumps(cfg, indent=2), encoding="utf-8")

    gui = appdata() / "gui.toml"
    write_gui_config(gui)
    col.config.load_config(str(gui), force=True)

    col.gui.desktop.connect(desktop_id=2)
    col.gui.desktop.wait_stable(desktop_id=2, timeout_s=5.0)
    before = snapshot_captures()
    start_monitor_process()

    verify_capture_appeared(key="first_shot", before=before, timeout_s=15.0)
    after_first = snapshot_captures()
    verify_no_capture(key="debounce_quiet", before=after_first, settle_s=8.0)

    from colosseum_gui.connections import close_all

    close_all()
    stop_tracked_processes()

    # New fixture phrase (different title so FlaUI can attach).
    write_gui_config(
        gui,
        fixture_title="PaFixture2",
        fixture_args=(
            f"fixture --phrase {DEBOUNCE_TRIGGER} --title PaFixture2 "
            f"--x 40 --y 40 --w 420 --h 320 --easy"
        ),
    )
    col.config.load_config(str(gui), force=True)
    col.gui.desktop.connect(desktop_id=2)
    col.gui.desktop.wait_stable(desktop_id=2, timeout_s=5.0)
    before2 = snapshot_captures()
    start_monitor_process()
    verify_capture_appeared(key="second_phrase", before=before2, timeout_s=15.0)
    close_all()
    stop_tracked_processes()


if __name__ == "__main__":
    main()
    col.endex()
