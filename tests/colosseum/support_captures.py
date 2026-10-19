"""Helpers for the pay_attention Colosseum suite (Windows / interactive session)."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import time
import uuid
import tempfile
import re

from colosseum.context import get_context
from PIL import Image
from pathlib import Path

import colosseum as col
from colosseum.decorators import VerificationResult, verification

REPO_ROOT = Path(__file__).resolve().parents[2]
APP_PROJECT = REPO_ROOT / "src" / "PayAttention" / "PayAttention.csproj"
HARNESS_PROJECT = (
    REPO_ROOT / "tests" / "PayAttention.Harness" / "PayAttention.Harness.csproj"
)
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


def require_live_execution() -> None:
    """These desktop scenarios require a live run; never replay raw side effects."""
    try:
        mode = get_context().active_execution_mode
    except RuntimeError:
        mode = "full"  # standalone entry points initialize configuration themselves
    if mode == "verify_only":
        raise RuntimeError(
            "pay_attention desktop scenarios require full or procedure mode"
        )


def evidence_dir() -> Path:
    cached = _STATE.get("evidence")
    if isinstance(cached, Path):
        return cached
    try:
        output = get_context().output_dir
    except RuntimeError:
        output = None
    root = (Path(output) if output else REPO_ROOT / "artifacts" / "colosseum") / (
        "evidence-" + uuid.uuid4().hex
    )
    root.mkdir(parents=True, exist_ok=True)
    _STATE["evidence"] = root
    return root


def ensure_built() -> None:
    require_live_execution()
    if _STATE.get("built"):
        return
    result = subprocess.run(
        ["dotnet", "build", "PayAttention.sln", "-c", "Release", "--nologo"],
        cwd=REPO_ROOT,
        check=True,
        timeout=180,
        env={**os.environ, "DOTNET_NOLOGO": "1"},
    )
    _STATE["built"] = result.returncode == 0


def run_harness(
    *mode_args: str, timeout_sec: float
) -> subprocess.CompletedProcess[str]:
    """Run the built executable; retain logs and product evidence even on timeout."""
    require_live_execution()
    ensure_built()
    root = evidence_dir() / ("harness-" + uuid.uuid4().hex)
    root.mkdir()
    cmd = [str(harness_exe()), *mode_args]
    proc = subprocess.Popen(
        cmd,
        cwd=REPO_ROOT,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        env={**os.environ, "PAY_ATTENTION_EVIDENCE": str(root)},
    )
    try:
        stdout, stderr = proc.communicate(timeout=timeout_sec)
    except subprocess.TimeoutExpired:
        # Kill only the process tree launched by this helper; wait before archiving output.
        subprocess.run(
            ["taskkill", "/PID", str(proc.pid), "/T", "/F"],
            capture_output=True,
            timeout=15,
            check=False,
        )
        stdout, stderr = proc.communicate(timeout=15)
        stderr += "\nHarness timed out; inspect retained evidence and stdout."
    # A killed harness cannot execute its finally block. Recover its printed owned root.
    for match in re.finditer(r"^Work dir: (.+)$", stdout, re.MULTILINE):
        source = Path(match.group(1).strip()).resolve()
        if (
            source.parent == Path(tempfile.gettempdir()).resolve()
            and source.name.startswith("pay_attention_e2e_")
            and source.is_dir()
        ):
            if any(p.is_symlink() or p.is_junction() for p in source.rglob("*")):
                raise RuntimeError(f"Linked harness evidence: {source}")
            shutil.copytree(
                source,
                root / source.name,
                dirs_exist_ok=True,
                ignore=shutil.ignore_patterns("*.dll", "*.exe", "*.pdb"),
            )
    (root / "stdout.txt").write_text(stdout, encoding="utf-8")
    (root / "stderr.txt").write_text(stderr, encoding="utf-8")
    return subprocess.CompletedProcess(cmd, proc.returncode, stdout, stderr)


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
    require_live_execution()
    root = Path(os.environ.get("TEMP", os.environ.get("TMP", "."))) / (
        "pa_colosseum_" + uuid.uuid4().hex[:10]
    )
    _STATE.setdefault("original_appdata", os.environ.get("PAY_ATTENTION_APPDATA"))
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
    require_live_execution()
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
    require_live_execution()
    if fixture_args is None:
        fixture_args = (
            f"fixture --phrase {SHOT_TRIGGER} --title {fixture_title} "
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
    require_live_execution()
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
    col.config.load_config(str(cfg), force=True)
    return cfg


def start_monitor_process() -> subprocess.Popen[str]:
    """Launch PayAttention.exe --start with isolated appdata (no window)."""
    require_live_execution()
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
    require_live_execution()
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
                proc.wait(timeout=5)
    procs.clear()


def snapshot_captures() -> set[str]:
    root = captures_dir()
    if not root.is_dir():
        return set()
    return {p.name for p in root.iterdir() if p.is_file()}


def completed_capture(
    path: Path, *, trigger_text: str | None = None, prove_pixels: bool = True
) -> bool:
    """Require an atomic completion receipt, exact trigger identity and decodable still."""
    if path.suffix.lower() not in {".jpg", ".jpeg", ".png", ".avi", ".gif"}:
        return False
    try:
        receipt = json.loads(
            path.with_suffix(".capture.json").read_text(encoding="utf-8")
        )
        if (
            not receipt.get("complete")
            or receipt.get("captureFile") != path.name
            or path.with_suffix(".pending").exists()
        ):
            return False
        config = json.loads((appdata() / "config.json").read_text(encoding="utf-8"))
        expected = {
            t["id"]
            for t in config["triggers"]
            if t.get("enabled", True)
            and (trigger_text is None or t["text"] == trigger_text)
        }
        if receipt.get("triggerId") not in expected:
            return False
        name = receipt.get("screenshotFile", "")
        if not name or Path(name).name != name:
            return False
        with Image.open(path.parent / name) as image:
            image.load()
        if prove_pixels:
            proof = run_harness(
                "inspect", str(path), str(appdata() / "config.json"), timeout_sec=45
            )
            if proof.returncode != 0:
                raise AssertionError(
                    "Saved capture content proof failed: " + harness_output(proof)
                )
        return True
    except (OSError, ValueError, KeyError):
        return False


def wait_for_new_capture(
    before: set[str],
    *,
    suffix: str = "_screenshot",
    timeout_s: float = CAPTURE_BUDGET_S,
    trigger_text: str | None = None,
) -> Path | None:
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        for path in captures_dir().iterdir():
            if (
                path.name not in before
                and (not suffix or suffix in path.name.lower())
                and completed_capture(path, trigger_text=trigger_text)
            ):
                return path
        time.sleep(0.1)
    return None


def no_new_capture(
    before: set[str], *, settle_s: float, suffix: str = "_screenshot"
) -> bool:
    deadline = time.monotonic() + settle_s
    while time.monotonic() < deadline:
        for path in captures_dir().iterdir():
            if (
                path.name not in before
                and (not suffix or suffix in path.name.lower())
                and completed_capture(path, prove_pixels=False)
            ):
                return False
        time.sleep(0.1)
    return True


def wait_for_status(message: str, *, after: int = 0, timeout_s: float = 5) -> None:
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        text = "".join(
            p.read_text(encoding="utf-8")
            for p in sorted((appdata() / "logs").glob("*.log"))
        )
        if message in text[after:]:
            return
        time.sleep(0.05)
    raise AssertionError(f"Monitor did not acknowledge {message!r}")


def log_size() -> int:
    return sum(
        len(p.read_text(encoding="utf-8"))
        for p in sorted((appdata() / "logs").glob("*.log"))
    )


def prepare_scope_bin() -> Path:
    """Copy harness as PaWhiteAlpha.exe / PaScopeDecoy.exe (distinct process names)."""
    require_live_execution()
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


def start_scope_fixture(
    exe_name: str, phrase: str, title: str, x: int
) -> subprocess.Popen[str]:
    require_live_execution()
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
    trigger_text: str | None = None,
) -> VerificationResult:
    """Pass when a completed, content-verified capture file appears under the isolated captures folder."""
    _ = key
    found = wait_for_new_capture(
        before, suffix=suffix, timeout_s=timeout_s, trigger_text=trigger_text
    )
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
    require_live_execution()
    stop_tracked_processes()
    root = _STATE.get("appdata")
    _STATE.pop("scope_bin", None)
    if isinstance(root, Path) and root.is_dir():
        resolved = root.resolve()
        parent = Path(tempfile.gettempdir()).resolve()
        if (
            resolved.parent != parent
            or not resolved.name.startswith("pa_colosseum_")
            or root.is_symlink()
        ):
            raise RuntimeError(f"Refusing unexpected cleanup root: {resolved}")
        # Archive before removal, including config, application logs and media.
        for entry in root.rglob("*"):
            if entry.is_symlink() or entry.is_junction():
                raise RuntimeError(f"Refusing linked evidence: {entry}")
        shutil.copytree(
            root, evidence_dir() / root.name, ignore=shutil.ignore_patterns("scope_bin")
        )
        shutil.rmtree(resolved)
    _STATE.pop("appdata", None)
    original = _STATE.pop("original_appdata", None)
    if original is None:
        os.environ.pop("PAY_ATTENTION_APPDATA", None)
    else:
        os.environ["PAY_ATTENTION_APPDATA"] = str(original)
