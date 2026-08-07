"""Contract tests for trigger matching (mirrors TriggerMatcher.cs)."""

from pay_attention_logic import TriggerConfig, find_match


def test_contains_match():
    triggers = [TriggerConfig(text="Error", match="contains", enabled=True)]
    result = find_match("An Error occurred", triggers)
    assert result is not None
    assert result.matched_portion == "Error"


def test_contains_case_insensitive():
    triggers = [TriggerConfig(text="error", match="contains", enabled=True)]
    result = find_match("Something ERROR here", triggers)
    assert result is not None


def test_exact_requires_full_equality():
    triggers = [TriggerConfig(text="Error", match="exact", enabled=True)]
    assert find_match("An Error occurred", triggers) is None
    result = find_match("Error", triggers)
    assert result is not None


def test_exact_normalizes_whitespace():
    triggers = [TriggerConfig(text="  hello   world ", match="exact", enabled=True)]
    result = find_match("hello\nworld", triggers)
    assert result is not None


def test_regex_match_and_non_match():
    triggers = [TriggerConfig(text=r"^Fail(ure)?$", match="regex", enabled=True)]
    assert find_match("Failure", triggers) is not None
    assert find_match("xFailure", triggers) is None


def test_disabled_triggers_ignored():
    triggers = [TriggerConfig(text="Error", match="contains", enabled=False)]
    assert find_match("An Error occurred", triggers) is None


def test_blank_ocr_no_match():
    triggers = [TriggerConfig(text="Error", match="contains", enabled=True)]
    assert find_match("", triggers) is None
    assert find_match("   ", triggers) is None


def test_blank_trigger_text_ignored():
    triggers = [TriggerConfig(text="  ", match="contains", enabled=True)]
    assert find_match("anything", triggers) is None


def test_invalid_regex_skipped():
    triggers = [
        TriggerConfig(text="[invalid", match="regex", enabled=True),
        TriggerConfig(text="ok", match="contains", enabled=True),
    ]
    result = find_match("this is ok", triggers)
    assert result is not None
    assert result.matched_portion == "ok"


def test_multi_whitespace_contains():
    triggers = [TriggerConfig(text="hello world", match="contains", enabled=True)]
    result = find_match("say   hello\t\nworld  please", triggers)
    assert result is not None


def test_underscore_and_space_equivalent():
    triggers = [TriggerConfig(text="PA_REC_2b8e1d", match="contains", enabled=True)]
    result = find_match("prefix PA REC 2b8e1d suffix", triggers)
    assert result is not None


def test_find_matches_returns_all():
    from pay_attention_logic import find_matches

    triggers = [
        TriggerConfig(text="zX9mQ2kP", match="contains", enabled=True),
        TriggerConfig(text="err#42v2", match="contains", enabled=True),
    ]
    results = find_matches("hello zX9mQ2kP and err#42v2 world", triggers)
    assert len(results) == 2


def test_confusable_fold_matches_ocr_mangling():
    triggers = [TriggerConfig(text="zX9mQ2kP", match="contains", enabled=True)]
    # OCR often confuses digits/lookalikes and inserts spaces
    assert find_match("noise zX9 mQ2 kP more", triggers) is not None
    assert find_match("prefix zX3mQ2kP suffix", triggers) is not None


def test_punctuation_in_trigger_optional():
    triggers = [TriggerConfig(text="err#42v2", match="contains", enabled=True)]
    assert find_match("status err42v2 done", triggers) is not None
    assert find_match("status err 42v2 done", triggers) is not None
