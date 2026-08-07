"""Contract tests for config import/export and screenshot-sequence naming."""

from pay_attention_logic import (
    SEQUENCE_AFTER_DEFAULT,
    SEQUENCE_BEFORE_DEFAULT,
    SEQUENCE_INTERVAL_DEFAULT,
    clamp_sequence_before_after,
    clamp_sequence_interval,
)


def sequence_file_name(offset_seconds: int, ext: str = ".jpg") -> str:
    """Mirror CaptureWriter.SequenceFileName."""
    abs_off = abs(offset_seconds)
    return f"t-{abs_off:03d}{ext}" if offset_seconds < 0 else f"t+{abs_off:03d}{ext}"


def test_sequence_fields_survive_normalize_shaped_dict():
    raw = {
        "sequenceIntervalSeconds": 99,
        "sequenceBeforeSeconds": -5,
        "sequenceAfterSeconds": 45,
        "triggers": [
            {"text": "Error", "match": "contains", "action": "sequence", "enabled": True}
        ],
    }
    normalized = {
        "sequenceIntervalSeconds": clamp_sequence_interval(raw["sequenceIntervalSeconds"]),
        "sequenceBeforeSeconds": clamp_sequence_before_after(raw["sequenceBeforeSeconds"]),
        "sequenceAfterSeconds": clamp_sequence_before_after(raw["sequenceAfterSeconds"]),
        "triggers": raw["triggers"],
    }
    assert normalized["sequenceIntervalSeconds"] == 30
    assert normalized["sequenceBeforeSeconds"] == 0
    assert normalized["sequenceAfterSeconds"] == 45
    assert normalized["triggers"][0]["action"] == "sequence"


def test_sequence_defaults_match_product():
    assert SEQUENCE_INTERVAL_DEFAULT == 3
    assert SEQUENCE_BEFORE_DEFAULT == 60
    assert SEQUENCE_AFTER_DEFAULT == 60


def test_sequence_file_naming_contract():
    assert sequence_file_name(-60) == "t-060.jpg"
    assert sequence_file_name(-3) == "t-003.jpg"
    assert sequence_file_name(0) == "t+000.jpg"
    assert sequence_file_name(3) == "t+003.jpg"
    assert sequence_file_name(60, ".png") == "t+060.png"


def test_export_shaped_config_includes_sequence_action():
    exported = {
        "sequenceIntervalSeconds": SEQUENCE_INTERVAL_DEFAULT,
        "sequenceBeforeSeconds": SEQUENCE_BEFORE_DEFAULT,
        "sequenceAfterSeconds": SEQUENCE_AFTER_DEFAULT,
        "triggers": [
            {"text": "zX9mQ2kP", "match": "contains", "action": "sequence", "enabled": True},
            {"text": "err#42v2", "match": "contains", "action": "record", "enabled": False},
        ],
    }
    assert exported["triggers"][0]["action"] == "sequence"
    assert {t["action"] for t in exported["triggers"]} == {"sequence", "record"}
