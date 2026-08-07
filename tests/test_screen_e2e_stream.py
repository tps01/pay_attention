"""End-to-end: streaming on-screen console; screenshot + recording + latency budget."""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[1]
HARNESS_PROJECT = REPO_ROOT / "tests" / "PayAttention.Harness" / "PayAttention.Harness.csproj"
LATENCY_BUDGET_MS = 15_000

pytestmark = pytest.mark.skipif(
    sys.platform != "win32" or shutil.which("dotnet") is None,
    reason="Windows + dotnet required for on-screen OCR harness",
)


def _parse_latency(stdout: str, kind: str) -> int:
    matches = re.findall(rf"LATENCY {kind}=(\d+)ms", stdout)
    assert matches, f"missing LATENCY {kind}=…ms in output:\n{stdout}"
    return int(matches[-1])


def test_streaming_console_screenshot_recording_and_latency():
    """
    Serial-console style: inject trigger lines over time into a dense buffer.
    Requires screenshot + recording, each detected within 15s of inject.
    """
    cmd = [
        "dotnet",
        "run",
        "--project",
        str(HARNESS_PROJECT),
        "-c",
        "Release",
        "--",
        "stream",
        "120",
    ]
    result = subprocess.run(
        cmd,
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        timeout=300,
        env={**os.environ, "DOTNET_NOLOGO": "1"},
    )

    output = (result.stdout or "") + "\n" + (result.stderr or "")
    assert result.returncode == 0, output
    assert "PASS" in result.stdout
    assert "screenshot:" in result.stdout.lower()
    assert "recording:" in result.stdout.lower()

    shot_ms = _parse_latency(result.stdout, "shot")
    rec_ms = _parse_latency(result.stdout, "rec")
    assert shot_ms <= LATENCY_BUDGET_MS, f"shot latency {shot_ms}ms > {LATENCY_BUDGET_MS}ms\n{output}"
    assert rec_ms <= LATENCY_BUDGET_MS, f"rec latency {rec_ms}ms > {LATENCY_BUDGET_MS}ms\n{output}"
