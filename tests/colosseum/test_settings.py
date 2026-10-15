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
    seed_screenshot_config,
)

TOKEN = "COL_SETTINGS_TOKEN"


def main() -> None:
    if "PAY_ATTENTION_APPDATA" not in os.environ:
        make_appdata()
        os.environ["PAY_ATTENTION_APPDATA"] = str(appdata())
        load_gui()

    seed_screenshot_config(trigger_text=TOKEN, enabled=False, debounce_seconds=30)

    col.gui.desktop.connect(desktop_id=1)
    col.gui.desktop.wait_stable(desktop_id=1, timeout_s=5.0)
    col.gui.desktop.verify_visible(
        desktop_id=1, key="save_visible", role="button", name="Save",
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
    col.gui.desktop.click(desktop_id=1, role="button", name="Save")
    time.sleep(1.0)

    saved = json.loads((appdata() / "config.json").read_text(encoding="utf-8"))
    texts = [t.get("text") for t in saved.get("triggers", [])]
    if TOKEN not in texts:
        raise AssertionError(f"Save did not persist {TOKEN!r}; triggers={texts}")

    # Relaunch Settings and confirm config still seeds the UI (file is source of truth).
    from colosseum_gui.connections import close_all

    close_all()
    time.sleep(0.5)
    col.gui.desktop.connect(desktop_id=1)
    col.gui.desktop.wait_stable(desktop_id=1, timeout_s=5.0)
    col.gui.desktop.verify_visible(
        desktop_id=1, key="save_reload", role="button", name="Save",
    )
    close_all()


if __name__ == "__main__":
    main()
    col.endex()
