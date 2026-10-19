"""Pairwise on-screen E2E: difficulty × scroll × speed × multi-pane × OCR × scope."""

from __future__ import annotations

import re

import colosseum as col

from support_captures import ensure_built, harness_output, run_harness

LATENCY_BUDGET_MS = 15_000

# Mirrors Harness PairwiseCases (1..10). Kind drives pass assertions.
PAIRWISE_CASES: list[tuple[int, str]] = [
    (1, "static-single"),
    (2, "multi"),
    (3, "multi"),
    (4, "scroll-single"),
    (5, "multi"),
    (6, "scroll-single"),
    (7, "static-single"),
    (8, "multi"),
    (9, "scroll-single"),
    (10, "multi"),
]


def _parse_latency(stdout: str, kind: str) -> int:
    matches = re.findall(rf"LATENCY {kind}=(\d+)ms", stdout)
    if not matches:
        raise AssertionError(f"missing LATENCY {kind}=…ms in output:\n{stdout}")
    return int(matches[-1])


def _run_case(case_id: int, kind: str) -> None:
    timeout_sec = 45 if kind == "multi" else 30
    result = run_harness(
        "pairwise",
        str(case_id),
        str(timeout_sec),
        timeout_sec=float(timeout_sec + 60),
    )
    output = harness_output(result)
    if result.returncode != 0:
        raise AssertionError(f"pairwise case {case_id} failed:\n{output}")
    if "PASS" not in (result.stdout or ""):
        raise AssertionError(f"pairwise case {case_id}: missing PASS\n{output}")
    if f"Pairwise: case={case_id}/" not in (result.stdout or ""):
        raise AssertionError(f"pairwise case {case_id}: missing case banner\n{output}")
    if "Mode: pairwise" not in (result.stdout or ""):
        raise AssertionError(
            f"pairwise case {case_id}: missing Mode: pairwise\n{output}"
        )

    stdout = result.stdout or ""
    if kind == "multi":
        if "alpha:" not in stdout.lower() or "beta:" not in stdout.lower():
            raise AssertionError(
                f"pairwise case {case_id}: expected alpha/beta captures\n{output}"
            )
        return

    if "screenshot:" not in stdout.lower() or "recording:" not in stdout.lower():
        raise AssertionError(
            f"pairwise case {case_id}: expected screenshot+recording\n{output}"
        )

    if kind == "scroll-single":
        if "LATENCY shot=" in stdout:
            shot_ms = _parse_latency(stdout, "shot")
            rec_ms = _parse_latency(stdout, "rec")
            if shot_ms > LATENCY_BUDGET_MS or rec_ms > LATENCY_BUDGET_MS:
                raise AssertionError(
                    f"pairwise case {case_id}: latency over budget "
                    f"(shot={shot_ms}ms rec={rec_ms}ms)\n{output}",
                )
        elif "LATENCY" not in stdout:
            raise AssertionError(f"pairwise case {case_id}: missing LATENCY\n{output}")


def main() -> None:
    from support_captures import require_live_execution

    require_live_execution()
    ensure_built()
    for case_id, kind in PAIRWISE_CASES:
        _run_case(case_id, kind)


if __name__ == "__main__":
    main()
    col.endex()
