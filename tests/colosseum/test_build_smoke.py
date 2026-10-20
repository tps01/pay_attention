"""Smoke: solution builds and xunit tests pass with dotnet."""

from __future__ import annotations

import os
import shutil
import subprocess

import colosseum as col

from support_captures import REPO_ROOT


def main() -> None:
    from support_captures import require_live_execution

    require_live_execution()
    if shutil.which("dotnet") is None:
        raise RuntimeError("dotnet SDK not on PATH")

    env = {**os.environ, "DOTNET_NOLOGO": "1"}
    from support_captures import ensure_built

    ensure_built()

    test_cmd = [
        "dotnet",
        "test",
        "tests/PayAttention.Tests/PayAttention.Tests.csproj",
        "-c",
        "Release",
        "--nologo",
    ]
    unit = subprocess.run(
        [*test_cmd, "--no-restore"],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        timeout=180,
        env=env,
    )
    if unit.returncode != 0:
        unit = subprocess.run(
            test_cmd,
            cwd=REPO_ROOT,
            capture_output=True,
            text=True,
            timeout=240,
            env=env,
        )
    if unit.returncode != 0:
        raise AssertionError(unit.stdout + "\n" + unit.stderr)


if __name__ == "__main__":
    main()
    col.endex()
