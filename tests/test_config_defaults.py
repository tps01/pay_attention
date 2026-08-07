"""Contract tests for product defaults (mirrors AppConfig / ConfigService)."""

from pay_attention_logic import (
    CAPTURE_SCOPE_DEFAULT,
    DEBUG_SAVE_JSON_SIDECARS_DEFAULT,
    OCR_INVERT_DEFAULT,
    OCR_MAX_WIDTH_DEFAULT,
    OCR_MAX_WIDTH_MAX,
    OCR_MAX_WIDTH_MIN,
    OCR_UPSCALE_DEFAULT,
    OCR_UPSCALE_MAX,
    OCR_UPSCALE_MIN,
    RECORD_FORMAT_DEFAULT,
    RECORD_JPEG_QUALITY_DEFAULT,
    RECORD_JPEG_QUALITY_MAX,
    RECORD_JPEG_QUALITY_MIN,
    SCREENSHOT_FORMAT_DEFAULT,
    SEQUENCE_AFTER_DEFAULT,
    SEQUENCE_BEFORE_AFTER_MAX,
    SEQUENCE_BEFORE_AFTER_MIN,
    SEQUENCE_BEFORE_DEFAULT,
    SEQUENCE_INTERVAL_DEFAULT,
    SEQUENCE_INTERVAL_MAX,
    SEQUENCE_INTERVAL_MIN,
    SAMPLE_TRIGGERS_DEFAULT,
    START_MONITORING_ON_LAUNCH_DEFAULT,
    clamp_ocr_max_width,
    clamp_ocr_upscale,
    clamp_record_jpeg_quality,
    clamp_sequence_before_after,
    clamp_sequence_interval,
)


def test_start_monitoring_default_off():
    assert START_MONITORING_ON_LAUNCH_DEFAULT is False


def test_json_sidecars_default_off():
    assert DEBUG_SAVE_JSON_SIDECARS_DEFAULT is False


def test_ocr_max_width_defaults():
    assert OCR_MAX_WIDTH_DEFAULT >= OCR_MAX_WIDTH_MIN
    assert OCR_MAX_WIDTH_MIN == 1600
    assert OCR_MAX_WIDTH_DEFAULT == 1920


def test_ocr_quality_defaults():
    assert CAPTURE_SCOPE_DEFAULT == "monitor"
    assert OCR_INVERT_DEFAULT is False
    assert OCR_UPSCALE_DEFAULT == 100
    assert OCR_UPSCALE_MIN == 100
    assert OCR_UPSCALE_MAX == 400


def test_recording_defaults():
    assert RECORD_FORMAT_DEFAULT == "avi"
    assert RECORD_JPEG_QUALITY_DEFAULT == 70
    assert RECORD_JPEG_QUALITY_MIN == 40
    assert RECORD_JPEG_QUALITY_MAX == 95
    assert SCREENSHOT_FORMAT_DEFAULT == "jpeg"


def test_sequence_defaults():
    assert SEQUENCE_INTERVAL_DEFAULT == 3
    assert SEQUENCE_INTERVAL_MIN == 1
    assert SEQUENCE_INTERVAL_MAX == 30
    assert SEQUENCE_BEFORE_DEFAULT == 60
    assert SEQUENCE_AFTER_DEFAULT == 60
    assert SEQUENCE_BEFORE_AFTER_MIN == 0
    assert SEQUENCE_BEFORE_AFTER_MAX == 60


def test_sample_triggers_disabled_by_default():
    assert len(SAMPLE_TRIGGERS_DEFAULT) == 2
    assert all(t["enabled"] is False for t in SAMPLE_TRIGGERS_DEFAULT)
    assert SAMPLE_TRIGGERS_DEFAULT[0]["text"] == "Error"
    assert SAMPLE_TRIGGERS_DEFAULT[1]["text"] == "Exception"
    assert SAMPLE_TRIGGERS_DEFAULT[0]["action"] == "screenshot"
    assert SAMPLE_TRIGGERS_DEFAULT[1]["action"] == "record"


def test_ocr_max_width_clamp():
    assert clamp_ocr_max_width(1280) == OCR_MAX_WIDTH_MIN
    assert clamp_ocr_max_width(1920) == 1920
    assert clamp_ocr_max_width(99999) == OCR_MAX_WIDTH_MAX


def test_ocr_upscale_clamp():
    assert clamp_ocr_upscale(50) == OCR_UPSCALE_MIN
    assert clamp_ocr_upscale(150) == 150
    assert clamp_ocr_upscale(999) == OCR_UPSCALE_MAX


def test_record_jpeg_quality_clamp():
    assert clamp_record_jpeg_quality(10) == RECORD_JPEG_QUALITY_MIN
    assert clamp_record_jpeg_quality(70) == 70
    assert clamp_record_jpeg_quality(99) == RECORD_JPEG_QUALITY_MAX


def test_sequence_clamps():
    assert clamp_sequence_interval(0) == SEQUENCE_INTERVAL_MIN
    assert clamp_sequence_interval(3) == 3
    assert clamp_sequence_interval(99) == SEQUENCE_INTERVAL_MAX
    assert clamp_sequence_before_after(-1) == SEQUENCE_BEFORE_AFTER_MIN
    assert clamp_sequence_before_after(30) == 30
    assert clamp_sequence_before_after(120) == SEQUENCE_BEFORE_AFTER_MAX
