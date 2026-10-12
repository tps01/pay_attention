"""Contract tests for product defaults (mirrors AppConfig / ConfigService)."""

from pay_attention_logic import (
    CAPTURE_SCOPE_DEFAULT,
    DEBUG_SAVE_JSON_SIDECARS_DEFAULT,
    LOG_LEVEL_DEFAULT,
    DEBOUNCE_SECONDS_DEFAULT,
    OCR_INTERVAL_MS_DEFAULT,
    OCR_INVERT_DEFAULT,
    OCR_MAX_WIDTH_DEFAULT,
    OCR_MAX_WIDTH_MAX,
    OCR_MAX_WIDTH_MIN,
    OCR_UPSCALE_DEFAULT,
    OCR_UPSCALE_MAX,
    OCR_UPSCALE_MIN,
    RECORD_BUFFER_SECONDS_DEFAULT,
    RECORD_FORMAT_DEFAULT,
    RECORD_FPS_DEFAULT,
    RECORD_JPEG_QUALITY_DEFAULT,
    RECORD_JPEG_QUALITY_MAX,
    RECORD_JPEG_QUALITY_MIN,
    RETENTION_MAX_AGE_DAYS_DEFAULT,
    RETENTION_MAX_FILES_DEFAULT,
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


def test_log_level_default_info():
    assert LOG_LEVEL_DEFAULT == "info"


def test_ocr_interval_and_debounce_defaults():
    assert OCR_INTERVAL_MS_DEFAULT == 500
    assert DEBOUNCE_SECONDS_DEFAULT == 30


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
    assert RECORD_BUFFER_SECONDS_DEFAULT == 15
    assert RECORD_FPS_DEFAULT == 3
    assert RECORD_JPEG_QUALITY_DEFAULT == 60
    assert RECORD_JPEG_QUALITY_MIN == 40
    assert RECORD_JPEG_QUALITY_MAX == 95
    assert SCREENSHOT_FORMAT_DEFAULT == "jpeg"


def test_sequence_defaults():
    assert SEQUENCE_INTERVAL_DEFAULT == 5
    assert SEQUENCE_INTERVAL_MIN == 1
    assert SEQUENCE_INTERVAL_MAX == 30
    assert SEQUENCE_BEFORE_DEFAULT == 30
    assert SEQUENCE_AFTER_DEFAULT == 30
    assert SEQUENCE_BEFORE_AFTER_MIN == 0
    assert SEQUENCE_BEFORE_AFTER_MAX == 60


def test_retention_defaults():
    assert RETENTION_MAX_AGE_DAYS_DEFAULT == 14
    assert RETENTION_MAX_FILES_DEFAULT == 200


def test_sample_triggers_disabled_by_default():
    assert len(SAMPLE_TRIGGERS_DEFAULT) == 1
    assert all(t["enabled"] is False for t in SAMPLE_TRIGGERS_DEFAULT)
    assert SAMPLE_TRIGGERS_DEFAULT[0]["text"] == "PAYATTN_EXAMPLE"
    assert SAMPLE_TRIGGERS_DEFAULT[0]["action"] == "screenshot"


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
    assert clamp_record_jpeg_quality(60) == 60
    assert clamp_record_jpeg_quality(99) == RECORD_JPEG_QUALITY_MAX


def test_sequence_clamps():
    assert clamp_sequence_interval(0) == SEQUENCE_INTERVAL_MIN
    assert clamp_sequence_interval(5) == 5
    assert clamp_sequence_interval(99) == SEQUENCE_INTERVAL_MAX
    assert clamp_sequence_before_after(-1) == SEQUENCE_BEFORE_AFTER_MIN
    assert clamp_sequence_before_after(30) == 30
    assert clamp_sequence_before_after(120) == SEQUENCE_BEFORE_AFTER_MAX
