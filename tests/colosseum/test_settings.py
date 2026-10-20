"""Settings window round-trip via FlaUI (not an OCR proof)."""

from __future__ import annotations

import json
import os
import time

import colosseum as col

from support_captures import (
    appdata,
    load_gui,
    make_appdata,
    release_desktop,
    seed_screenshot_config,
)

TOKEN = "COL_SETTINGS_TOKEN"


def main() -> None:
    from support_captures import require_live_execution

    require_live_execution()
    if "PAY_ATTENTION_APPDATA" not in os.environ:
        make_appdata()
        os.environ["PAY_ATTENTION_APPDATA"] = str(appdata())
        load_gui()

    seed_screenshot_config(trigger_text=TOKEN, enabled=False, debounce_seconds=30)

    try:
        _edit_and_relaunch()
    finally:
        release_desktop()


def _edit_and_relaunch() -> None:
    col.gui.desktop.connect(desktop_id=1)
    col.gui.desktop.wait_stable(desktop_id=1, timeout_s=5.0)
    col.gui.desktop.verify_visible(
        desktop_id=1,
        key="save_visible",
        role="button",
        name="Save",
    )
    # Best-effort UIA: DataGrid cells expose the trigger Name when available.
    col.gui.desktop.verify_text(
        desktop_id=1,
        key="trigger_row",
        expected=TOKEN,
        name=TOKEN,
        optional=True,
    )
    col.gui.desktop.capture_screenshot(desktop_id=1, path="captures/settings.png")
    # Focus selects the seeded value so these keystrokes replace it.
    col.gui.desktop.type_text(
        desktop_id=1, automation_id="DebounceBox", text="17", input="keys"
    )
    col.gui.desktop.click(desktop_id=1, role="button", name="Save")
    time.sleep(1.0)

    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        saved = json.loads((appdata() / "config.json").read_text(encoding="utf-8"))
        if saved.get("debounceSeconds") == 17:
            break
        time.sleep(0.1)
    else:
        raise AssertionError(
            "Save did not persist the edited debounce value (30 -> 17)"
        )
    col.gui.desktop.press_key(desktop_id=1, key="enter")

    # Relaunch Settings and confirm config still seeds the UI (file is source of truth).
    from colosseum_gui.connections import close_all

    close_all()
    time.sleep(0.5)
    col.gui.desktop.connect(desktop_id=1)
    col.gui.desktop.wait_stable(desktop_id=1, timeout_s=5.0)
    col.gui.desktop.verify_visible(
        desktop_id=1,
        key="save_reload",
        role="button",
        name="Save",
    )
    # get_text reads the control name, which is empty on this box. Saving again
    # writes whatever the reloaded form is showing.
    col.gui.desktop.click(desktop_id=1, role="button", name="Save")
    time.sleep(0.5)
    col.gui.desktop.press_key(desktop_id=1, key="enter")
    reloaded = json.loads((appdata() / "config.json").read_text(encoding="utf-8"))
    if reloaded.get("debounceSeconds") != 17:
        raise AssertionError(
            f"Reloaded settings did not keep debounce 17: {reloaded.get('debounceSeconds')}"
        )
    close_all()


if __name__ == "__main__":
    main()
    col.endex()
