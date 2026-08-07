"""Smoke: solution builds and unit tests pass with dotnet."""

from __future__ import annotations

import shutil
import subprocess
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[1]


@pytest.mark.skipif(shutil.which("dotnet") is None, reason="dotnet SDK not on PATH")
def test_dotnet_build_release():
    result = subprocess.run(
        ["dotnet", "build", "PayAttention.sln", "-c", "Release", "--nologo"],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        timeout=180,
    )
    assert result.returncode == 0, result.stdout + "\n" + result.stderr


@pytest.mark.skipif(shutil.which("dotnet") is None, reason="dotnet SDK not on PATH")
def test_dotnet_unit_tests():
    result = subprocess.run(
        [
            "dotnet",
            "test",
            "tests/PayAttention.Tests/PayAttention.Tests.csproj",
            "-c",
            "Release",
            "--nologo",
            "--no-restore",
        ],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        timeout=180,
    )
    if result.returncode != 0:
        # First run may need restore
        result = subprocess.run(
            [
                "dotnet",
                "test",
                "tests/PayAttention.Tests/PayAttention.Tests.csproj",
                "-c",
                "Release",
                "--nologo",
            ],
            cwd=REPO_ROOT,
            capture_output=True,
            text=True,
            timeout=240,
        )
    assert result.returncode == 0, result.stdout + "\n" + result.stderr
