"""Pairwise on-screen E2E covering difficulty × scroll × speed × multi-pane × OCR × scope."""

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

# Mirrors Harness PairwiseCases (1..8). Kind drives pass assertions.
PAIRWISE_CASES = [
    pytest.param(1, "static-single", id="01-easy-static-slow-single-fast-white"),
    pytest.param(2, "multi", id="02-easy-scroll-fast-multi-thorough-black"),
    pytest.param(3, "multi", id="03-hard-static-fast-multi-fast-black"),
    pytest.param(4, "scroll-single", id="04-hard-scroll-slow-single-thorough-white"),
    pytest.param(5, "multi", id="05-easy-static-fast-multi-thorough-white"),
    pytest.param(6, "scroll-single", id="06-easy-scroll-slow-single-fast-black"),
    pytest.param(7, "static-single", id="07-hard-static-slow-single-thorough-black"),
    pytest.param(8, "multi", id="08-hard-scroll-fast-multi-fast-white"),
]


def _parse_latency(stdout: str, kind: str) -> int:
    matches = re.findall(rf"LATENCY {kind}=(\d+)ms", stdout)
    assert matches, f"missing LATENCY {kind}=…ms in output:\n{stdout}"
    return int(matches[-1])


@pytest.mark.parametrize("case_id,kind", PAIRWISE_CASES)
def test_pairwise_screen_e2e(case_id: int, kind: str):
    """
    One row of the pairwise covering array. Reuses hard / stream / dmesg / scope
    harness paths with difficulty, OCR mode, and scope applied.
    """
    # Fail fast: ~1/4 of the old 120s/180s harness budgets.
    timeout_sec = 45 if kind == "multi" else 30
    cmd = [
        "dotnet",
        "run",
        "--project",
        str(HARNESS_PROJECT),
        "-c",
        "Release",
        "--",
        "pairwise",
        str(case_id),
        str(timeout_sec),
    ]
    result = subprocess.run(
        cmd,
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        timeout=timeout_sec + 60,
        env={**os.environ, "DOTNET_NOLOGO": "1"},
    )

    output = (result.stdout or "") + "\n" + (result.stderr or "")
    assert result.returncode == 0, output
    assert "PASS" in result.stdout
    assert f"Pairwise: case={case_id}/" in result.stdout
    assert "Mode: pairwise" in result.stdout

    if kind == "multi":
        assert "alpha:" in result.stdout.lower()
        assert "beta:" in result.stdout.lower()
        return

    assert "screenshot:" in result.stdout.lower()
    assert "recording:" in result.stdout.lower()

    if kind == "scroll-single":
        # Stream uses shot/rec; dmesg uses shot/rec aliases for the same inject.
        if "LATENCY shot=" in result.stdout:
            assert _parse_latency(result.stdout, "shot") <= LATENCY_BUDGET_MS, output
            assert _parse_latency(result.stdout, "rec") <= LATENCY_BUDGET_MS, output
        else:
            # dmesg prints LATENCY shot= and LATENCY rec= for RIP inject as well
            assert "LATENCY" in result.stdout, output
