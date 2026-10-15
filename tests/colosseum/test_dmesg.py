"""End-to-end: fast dmesg/MCE stream; screenshot + recording + latency."""

from __future__ import annotations

import re

import colosseum as col

from support_captures import ensure_built, harness_output, run_harness

LATENCY_BUDGET_MS = 15_000


def _parse_latency(stdout: str, kind: str) -> int:
    matches = re.findall(rf"LATENCY {kind}=(\d+)ms", stdout)
    if not matches:
        raise AssertionError(f"missing LATENCY {kind}=…ms in output:\n{stdout}")
    return int(matches[-1])


def main() -> None:
    """
    Fast-flowing white/red dmesg; MCE scrolls through mid-stream (no pause).
    Single trigger phrase: RIP !INEXACT! (screenshot + record).
    """
    ensure_built()
    result = run_harness("dmesg", "120", timeout_sec=300.0)
    output = harness_output(result)
    if result.returncode != 0:
        raise AssertionError(output)
    stdout = result.stdout or ""
    if "PASS" not in stdout:
        raise AssertionError(f"missing PASS\n{output}")
    if "screenshot:" not in stdout.lower() or "recording:" not in stdout.lower():
        raise AssertionError(f"expected screenshot and recording\n{output}")

    shot_ms = _parse_latency(stdout, "shot")
    rec_ms = _parse_latency(stdout, "rec")
    if shot_ms > LATENCY_BUDGET_MS:
        raise AssertionError(f"shot latency {shot_ms}ms > {LATENCY_BUDGET_MS}ms\n{output}")
    if rec_ms > LATENCY_BUDGET_MS:
        raise AssertionError(f"rec latency {rec_ms}ms > {LATENCY_BUDGET_MS}ms\n{output}")


if __name__ == "__main__":
    main()
    col.endex()
