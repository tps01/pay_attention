"""End-to-end: 50 triggers, one on-screen match at first / mid / last list position."""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[1]
HARNESS_PROJECT = REPO_ROOT / "tests" / "PayAttention.Harness" / "PayAttention.Harness.csproj"

pytestmark = pytest.mark.skipif(
    sys.platform != "win32" or shutil.which("dotnet") is None,
    reason="Windows + dotnet required for on-screen OCR harness",
)


@pytest.mark.parametrize("slot", ["first", "mid", "last"])
def test_many_triggers_one_match_by_list_position(slot: str):
    """
    Hard fixture with zX9mQ2kP on screen. Config has 50 Screenshot triggers; only
    that phrase is real. Slot places it first, 25th, or last in the list.
    """
    cmd = [
        "dotnet",
        "run",
        "--project",
        str(HARNESS_PROJECT),
        "-c",
        "Release",
        "--",
        "many",
        slot,
        "90",
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
    assert f"Real trigger slot: {slot}" in result.stdout
