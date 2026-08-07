"""End-to-end: hard on-screen OCR fixture; screenshot + recording required."""

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


def test_hard_screen_fixture_screenshot_and_recording():
    """
    Tiny dark-terminal text, weird tokens, and clutter. Asserts both a screenshot
    and a recording capture from the on-screen harness.
    """
    cmd = [
        "dotnet",
        "run",
        "--project",
        str(HARNESS_PROJECT),
        "-c",
        "Release",
        "--",
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
    assert "recording:" in result.stdout.lower()
