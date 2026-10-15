"""End-to-end: 50 triggers, one on-screen match at first / mid / last list position."""

from __future__ import annotations

import colosseum as col

from support_captures import ensure_built, harness_output, run_harness


def _run_slot(slot: str) -> None:
    result = run_harness("many", slot, "90", timeout_sec=300.0)
    output = harness_output(result)
    if result.returncode != 0:
        raise AssertionError(f"many slot={slot} failed:\n{output}")
    stdout = result.stdout or ""
    if "PASS" not in stdout:
        raise AssertionError(f"many slot={slot}: missing PASS\n{output}")
    if "screenshot:" not in stdout.lower():
        raise AssertionError(f"many slot={slot}: missing screenshot\n{output}")
    if f"Real trigger slot: {slot}" not in stdout:
        raise AssertionError(f"many slot={slot}: missing slot banner\n{output}")


def main() -> None:
    ensure_built()
    for slot in ("first", "mid", "last"):
        _run_slot(slot)


if __name__ == "__main__":
    main()
    col.endex()
