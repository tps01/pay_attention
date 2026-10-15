"""Helpers for the pay_attention Colosseum suite (Windows / interactive session)."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import time
import uuid
from pathlib import Path

import colosseum as col
from colosseum.decorators import VerificationResult, verification

REPO_ROOT = Path(__file__).resolve().parents[2]
APP_PROJECT = REPO_ROOT / "src" / "PayAttention" / "PayAttention.csproj"
HARNESS_PROJECT = REPO_ROOT / "tests" / "PayAttention.Harness" / "PayAttention.Harness.csproj"
TFM = "net8.0-windows10.0.19041.0"
CAPTURE_BUDGET_S = 15.0
SHOT_TRIGGER = "zX9mQ2kP"
REC_TRIGGER = "zRecOK42"
DEBOUNCE_TRIGGER = "mR4tL8vC"
SCOPE_ALPHA = "kA7nP3qW"
SCOPE_DECOY = "bZ2wH6yD"

_STATE: dict[str, object] = {}


def repo_root() -> Path:
    return REPO_ROOT


def ensure_built() -> None:
    for project in (APP_PROJECT, HARNESS_PROJECT):
        subprocess.run(
            ["dotnet", "build", str(project), "-c", "Release", "--nologo"],
            cwd=REPO_ROOT,
            check=True,
            env={**os.environ, "DOTNET_NOLOGO": "1"},
        )


def run_harness(
    *mode_args: str,
    timeout_sec: float,
) -> subprocess.CompletedProcess[str]:
    """Run PayAttention.Harness in Release (``dotnet run``) and return the result."""
    cmd = [
        "dotnet",
        "run",
        "--project",
        str(HARNESS_PROJECT),
        "-c",
        "Release",
        "--no-build",
        "--",
        *mode_args,
    ]
    return subprocess.run(
        cmd,
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        timeout=timeout_sec,
        env={**os.environ, "DOTNET_NOLOGO": "1"},
        check=False,
    )


def harness_output(result: subprocess.CompletedProcess[str]) -> str:
    return (result.stdout or "") + "\n" + (result.stderr or "")


def app_exe() -> Path:
    path = (
        REPO_ROOT
        / "src"
        / "PayAttention"
        / "bin"
        / "Release"
        / TFM
        / "PayAttention.exe"
    )
    if not path.is_file():
        raise FileNotFoundError(path)
    return path


def harness_exe() -> Path:
    path = (
        REPO_ROOT
        / "tests"
        / "PayAttention.Harness"
        / "bin"
        / "Release"
        / TFM
        / "PayAttention.Harness.exe"
    )
    if not path.is_file():
        raise FileNotFoundError(path)
    return path


def make_appdata() -> Path:
    root = Path(os.environ.get("TEMP", os.environ.get("TMP", "."))) / (
        "pa_colosseum_" + uuid.uuid4().hex[:10]
    )
    (root / "captures").mkdir(parents=True, exist_ok=True)
    (root / "logs").mkdir(parents=True, exist_ok=True)
    _STATE["appdata"] = root
    return root


def appdata() -> Path:
    root = _STATE.get("appdata")
    if not isinstance(root, Path):
        raise RuntimeError("call make_appdata() first")
    return root


def captures_dir() -> Path:
    return appdata() / "captures"


def write_config(config: dict) -> Path:
    path = appdata() / "config.json"
    path.write_text(json.dumps(config, indent=2), encoding="utf-8")
    return path


def seed_screenshot_config(
    *,
    trigger_text: str = SHOT_TRIGGER,
    debounce_seconds: int = 30,
    enabled: bool = True,
    applications: list[dict] | None = None,
    application_scope: str = "allMinusBlacklisted",
) -> dict:
    trigger_id = str(uuid.uuid4())
    config = {
        "ocrIntervalMs": 500,
        "debounceSeconds": debounce_seconds,
        "startMonitoringOnLaunch": False,
        "startWithWindows": False,
        "captureDirectory": str(captures_dir()),
        "screenshotFormat": "jpeg",
        "applicationScope": application_scope,
        "applications": applications or [],
        "triggers": [
            {
                "id": trigger_id,
                "text": trigger_text,
                "match": "contains",
                "action": "screenshot",
                "ocrMode": "thorough",
                "enabled": enabled,
            }
        ],
        "retentionMaxAgeDays": 0,
        "retentionMaxFiles": 0,
        "pauseHotkey": "Ctrl+Shift+P",
        "logLevel": "debug",
    }
    write_config(config)
    return config


def write_gui_config(
    path: Path,
    *,
    settings_title: str = "pay_attention — Settings",
    fixture_title: str = "PaFixture",
    fixture_args: str | None = None,
) -> Path:
    """TOML for col.gui.desktop: settings (1), fixture (2), decoy fixture (3)."""
    if fixture_args is None:
        fixture_args = (
            f'fixture --phrase {SHOT_TRIGGER} --title {fixture_title} '
            f"--x 40 --y 40 --w 420 --h 320 --easy"
        )
    decoy_args = (
        f"fixture --phrase {SCOPE_DECOY} --title PaDecoy "
        f"--x 500 --y 40 --w 420 --h 320 --easy"
    )
    text = "\n".join(
        [
            "[[gui.desktop]]",
            "desktop_id = 1",
            'driver = "flaui"',
            f'exe = "{_toml_path(app_exe())}"',
            'args = "--settings"',
            f'title = "{settings_title}"',
            "timeout_s = 30",
            "",
            "[[gui.desktop]]",
            "desktop_id = 2",
            'driver = "flaui"',
            f'exe = "{_toml_path(harness_exe())}"',
            f'args = "{fixture_args}"',
            f'title = "{fixture_title}"',
            "timeout_s = 30",
            "",
            "[[gui.desktop]]",
            "desktop_id = 3",
            'driver = "flaui"',
            f'exe = "{_toml_path(harness_exe())}"',
            f'args = "{decoy_args}"',
            'title = "PaDecoy"',
            "timeout_s = 30",
            "",
        ]
    )
    path.write_text(text, encoding="utf-8")
    return path


def _toml_path(path: Path) -> str:
    return str(path.resolve()).replace("\\", "/")


def load_gui() -> Path:
    ensure_built()
    if "appdata" not in _STATE:
        make_appdata()
    try:
        from colosseum_gui.connections import close_all

        close_all()
    except Exception:  # noqa: BLE001
        pass
    cfg = appdata() / "gui.toml"
    write_gui_config(cfg)
    col.config.load_config(str(cfg))
    return cfg


def start_monitor_process() -> subprocess.Popen[str]:
    """Launch PayAttention.exe --start with isolated appdata (no window)."""
    env = {
        **os.environ,
        "PAY_ATTENTION_APPDATA": str(appdata()),
        "DOTNET_NOLOGO": "1",
    }
    proc = subprocess.Popen(
        [str(app_exe()), "--start"],
        cwd=str(app_exe().parent),
        env=env,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    procs = _STATE.setdefault("procs", [])
    assert isinstance(procs, list)
    procs.append(proc)
    time.sleep(2.0)
    return proc


def stop_tracked_processes() -> None:
    procs = _STATE.get("procs")
    if not isinstance(procs, list):
        return
    for proc in procs:
        if not isinstance(proc, subprocess.Popen):
            continue
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()
    procs.clear()


def snapshot_captures() -> set[str]:
    root = captures_dir()
    if not root.is_dir():
        return set()
    return {p.name for p in root.iterdir() if p.is_file()}


def wait_for_new_capture(
    before: set[str],
    *,
    suffix: str = "_screenshot",
    timeout_s: float = CAPTURE_BUDGET_S,
) -> Path | None:
    deadline = time.monotonic() + timeout_s
    root = captures_dir()
    while time.monotonic() < deadline:
        for path in root.iterdir():
            if not path.is_file() or path.name in before:
                continue
            if suffix and suffix not in path.name.lower():
                continue
            if path.suffix.lower() in {".jpg", ".jpeg", ".png", ".avi", ".gif"}:
                return path
        time.sleep(0.25)
    return None


def no_new_capture(
    before: set[str],
    *,
    settle_s: float,
    suffix: str = "_screenshot",
) -> bool:
    time.sleep(settle_s)
    return wait_for_new_capture(before, suffix=suffix, timeout_s=0.1) is None


def prepare_scope_bin() -> Path:
    """Copy harness as PaWhiteAlpha.exe / PaScopeDecoy.exe (distinct process names)."""
    bin_dir = appdata() / "scope_bin"
    bin_dir.mkdir(parents=True, exist_ok=True)
    src_dir = harness_exe().parent
    for file in src_dir.iterdir():
        if file.is_file():
            shutil.copy2(file, bin_dir / file.name)
    alpha = bin_dir / "PaWhiteAlpha.exe"
    decoy = bin_dir / "PaScopeDecoy.exe"
    shutil.copy2(harness_exe(), alpha)
    shutil.copy2(harness_exe(), decoy)
    _STATE["scope_bin"] = bin_dir
    return bin_dir


def start_scope_fixture(exe_name: str, phrase: str, title: str, x: int) -> subprocess.Popen[str]:
    bin_dir = _STATE.get("scope_bin")
    if not isinstance(bin_dir, Path):
        bin_dir = prepare_scope_bin()
    exe = bin_dir / exe_name
    args = (
        f"fixture --phrase {phrase} --title {title} "
        f"--x {x} --y 40 --w 400 --h 300 --easy"
    )
    proc = subprocess.Popen(
        [str(exe), *args.split()],
        cwd=str(bin_dir),
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    procs = _STATE.setdefault("procs", [])
    assert isinstance(procs, list)
    procs.append(proc)
    time.sleep(1.5)
    return proc


@verification
def verify_capture_appeared(
    *,
    key: str,
    before: set[str],
    suffix: str = "_screenshot",
    timeout_s: float = CAPTURE_BUDGET_S,
    optional: bool = False,
) -> VerificationResult:
    """Pass when a new capture file appears under the isolated captures folder."""
    _ = key
    found = wait_for_new_capture(before, suffix=suffix, timeout_s=timeout_s)
    if found is not None:
        return VerificationResult(status="PASS", message=f"capture {found.name}")
    return VerificationResult(
        status="FAIL",
        message=f"no new {suffix} file within {timeout_s:.0f}s",
        optional=optional,
    )


@verification
def verify_no_capture(
    *,
    key: str,
    before: set[str],
    settle_s: float,
    suffix: str = "_screenshot",
    optional: bool = False,
) -> VerificationResult:
    """Pass when no new capture appears during settle_s."""
    _ = key
    if no_new_capture(before, settle_s=settle_s, suffix=suffix):
        return VerificationResult(status="PASS", message=f"quiet for {settle_s:.1f}s")
    return VerificationResult(
        status="FAIL",
        message=f"unexpected capture while waiting {settle_s:.1f}s",
        optional=optional,
    )


def cleanup_appdata() -> None:
    stop_tracked_processes()
    root = _STATE.pop("appdata", None)
    _STATE.pop("scope_bin", None)
    if isinstance(root, Path) and root.is_dir():
        shutil.rmtree(root, ignore_errors=True)
