"""Offline regression checks for the live-suite orchestration and evidence gates."""

import importlib
import json
import sys
import tempfile
from pathlib import Path
from types import SimpleNamespace

import pytest
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "colosseum"))
import support_captures as support


@pytest.fixture(autouse=True)
def state(monkeypatch):
    support._STATE.clear()
    monkeypatch.setattr(
        support,
        "get_context",
        lambda: SimpleNamespace(active_execution_mode="full", output_dir=None),
    )
    yield
    support._STATE.clear()


def setup_capture(tmp_path, *, trigger_id="expected"):
    support._STATE["appdata"] = tmp_path
    (tmp_path / "captures").mkdir()
    (tmp_path / "config.json").write_text(
        json.dumps({"triggers": [{"id": "expected", "text": "TOKEN", "enabled": True}]})
    )
    path = tmp_path / "captures" / "event_screenshot.jpg"
    Image.new("RGB", (20, 20)).save(path)
    path.with_suffix(".capture.json").write_text(
        json.dumps(
            {
                "complete": True,
                "captureFile": path.name,
                "screenshotFile": path.name,
                "triggerId": trigger_id,
            }
        )
    )
    return path


def test_file_gate_rejects_empty_partial_and_wrong_trigger_media(tmp_path):
    path = setup_capture(tmp_path)
    assert support.completed_capture(path, trigger_text="TOKEN", prove_pixels=False)
    assert not support.completed_capture(
        path, trigger_text="ANOTHER", prove_pixels=False
    )
    path.with_suffix(".pending").touch()
    assert not support.completed_capture(path, prove_pixels=False)
    path.with_suffix(".pending").unlink()
    path.write_bytes(b"")
    assert not support.completed_capture(path, prove_pixels=False)


def test_content_proof_failure_cannot_pass(tmp_path, monkeypatch):
    path = setup_capture(tmp_path)
    monkeypatch.setattr(
        support,
        "run_harness",
        lambda *a, **k: SimpleNamespace(returncode=1, stdout="", stderr="wrong pixels"),
    )
    with pytest.raises(AssertionError, match="content proof failed"):
        support.completed_capture(path)


def test_unrelated_trigger_receipt_cannot_pass(tmp_path):
    path = setup_capture(tmp_path, trigger_id="unrelated")
    assert not support.completed_capture(path, prove_pixels=False)


def test_verify_only_rejects_every_script_before_side_effects(monkeypatch):
    monkeypatch.setattr(
        support,
        "get_context",
        lambda: SimpleNamespace(active_execution_mode="verify_only"),
    )
    for path in (support.REPO_ROOT / "tests" / "colosseum").glob("*.py"):
        if path.name == "support_captures.py":
            continue
        module = importlib.import_module(path.stem)
        with pytest.raises(RuntimeError, match="require full"):
            module.main()
    with pytest.raises(RuntimeError, match="require full"):
        support.ensure_built()
    with pytest.raises(RuntimeError, match="require full"):
        support.make_appdata()


def test_build_is_cached_only_after_success(monkeypatch):
    calls = []
    monkeypatch.setattr(
        support.subprocess,
        "run",
        lambda *a, **k: calls.append(a) or SimpleNamespace(returncode=0),
    )
    support.ensure_built()
    support.ensure_built()
    assert len(calls) == 1


def test_cleanup_archives_application_evidence_before_removing_isolated_data(
    tmp_path, monkeypatch
):
    root = Path(tempfile.mkdtemp(prefix="pa_colosseum_"))
    (root / "logs").mkdir()
    (root / "logs" / "app.log").write_text("evidence")
    support._STATE["appdata"] = root
    monkeypatch.setattr(support, "evidence_dir", lambda: tmp_path)
    support.cleanup_appdata()
    assert not root.exists()
    assert (tmp_path / root.name / "logs" / "app.log").read_text() == "evidence"


def test_archive_failure_preserves_original_evidence(tmp_path, monkeypatch):
    root = Path(tempfile.mkdtemp(prefix="pa_colosseum_"))
    (root / "evidence.txt").write_text("keep me")
    support._STATE["appdata"] = root
    monkeypatch.setattr(support, "evidence_dir", lambda: tmp_path)
    with monkeypatch.context() as patch:
        patch.setattr(
            support.shutil,
            "copytree",
            lambda *a, **k: (_ for _ in ()).throw(OSError("disk full")),
        )
        with pytest.raises(OSError, match="disk full"):
            support.cleanup_appdata()
    assert (root / "evidence.txt").read_text() == "keep me"
    support.cleanup_appdata()


def test_pairwise_array_covers_all_binary_pairs():
    import itertools
    import re

    source = (
        support.REPO_ROOT / "tests" / "PayAttention.Harness" / "Program.cs"
    ).read_text(encoding="utf-8")
    array = source.split("private static readonly HarnessOpts[] PairwiseCases =", 1)[
        1
    ].split("];", 1)[0]
    rows = re.findall(
        r"new\(Easy: (true|false), Scrolling: (true|false), FastScroll: (true|false), MultiPane: (true|false), OcrMode: OcrSpeedMode\.(Fast|Thorough), WhitelistScope: (true|false)\)",
        array,
    )
    values = [("true", "false")] * 4 + [("Fast", "Thorough"), ("true", "false")]
    for first, second in itertools.combinations(range(6), 2):
        assert {(row[first], row[second]) for row in rows} == set(
            itertools.product(values[first], values[second])
        )
