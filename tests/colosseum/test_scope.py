"""Whitelist Selected: allowed fixture captures; decoy process does not."""

from __future__ import annotations

import os
import time
import uuid

import colosseum as col

from support_captures import (
    SCOPE_ALPHA,
    SCOPE_DECOY,
    appdata,
    make_appdata,
    prepare_scope_bin,
    snapshot_captures,
    start_monitor_process,
    start_scope_fixture,
    stop_tracked_processes,
    verify_capture_appeared,
    write_config,
)


def main() -> None:
    from support_captures import require_live_execution

    require_live_execution()
    if "PAY_ATTENTION_APPDATA" not in os.environ:
        make_appdata()
        os.environ["PAY_ATTENTION_APPDATA"] = str(appdata())

    alpha_id = str(uuid.uuid4())
    decoy_id = str(uuid.uuid4())
    write_config(
        {
            "ocrIntervalMs": 500,
            "debounceSeconds": 60,
            "startMonitoringOnLaunch": False,
            "startWithWindows": False,
            "captureDirectory": str(appdata() / "captures"),
            "screenshotFormat": "jpeg",
            "applicationScope": "whitelisted",
            "applications": [
                {
                    "id": str(uuid.uuid4()),
                    "processName": "PaWhiteAlpha",
                    "enabled": True,
                    "listMode": "whitelist",
                    "triggerFilter": "selected",
                    "triggerIds": [alpha_id],
                    "debounceSeconds": 60,
                }
            ],
            "triggers": [
                {
                    "id": alpha_id,
                    "text": SCOPE_ALPHA,
                    "match": "contains",
                    "action": "screenshot",
                    "ocrMode": "thorough",
                    "enabled": True,
                },
                {
                    "id": decoy_id,
                    "text": SCOPE_DECOY,
                    "match": "contains",
                    "action": "screenshot",
                    "ocrMode": "thorough",
                    "enabled": True,
                },
            ],
            "retentionMaxAgeDays": 0,
            "retentionMaxFiles": 0,
            "pauseHotkey": "Ctrl+Shift+P",
            "logLevel": "debug",
        }
    )

    prepare_scope_bin()
    # Decoy first (left), then allowed pane (right) so whitelist FG is the recent one.
    start_scope_fixture("PaScopeDecoy.exe", SCOPE_DECOY, "PaDecoy", x=40)
    start_scope_fixture("PaWhiteAlpha.exe", SCOPE_ALPHA, "PaAlpha", x=500)
    time.sleep(1.0)

    before = snapshot_captures()
    start_monitor_process()
    verify_capture_appeared(
        key="whitelist_alpha", before=before, timeout_s=20.0, trigger_text=SCOPE_ALPHA
    )

    # Only the alpha trigger may fire. Confirm no decoy-named capture appeared.
    captures = list((appdata() / "captures").glob("*"))
    decoy_safe = "bz2wh6yd"
    decoy_hits = [p.name for p in captures if decoy_safe in p.name.lower()]
    if decoy_hits:
        raise AssertionError(f"decoy captures should not appear: {decoy_hits}")

    stop_tracked_processes()


if __name__ == "__main__":
    main()
    col.endex()
