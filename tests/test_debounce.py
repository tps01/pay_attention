"""Contract tests for debounce (mirrors DebounceTracker.cs)."""

import time

from pay_attention_logic import DebounceTracker


def test_same_key_debounce_window():
    tracker = DebounceTracker()
    assert tracker.should_fire("Error", 0.2) is True
    assert tracker.should_fire("Error", 0.2) is False
    time.sleep(0.25)
    assert tracker.should_fire("Error", 0.2) is True


def test_different_keys_independent():
    tracker = DebounceTracker()
    assert tracker.should_fire("a", 1.0) is True
    assert tracker.should_fire("b", 1.0) is True
    assert tracker.should_fire("a", 1.0) is False


def test_case_insensitive_keys():
    tracker = DebounceTracker()
    assert tracker.should_fire("Error", 1.0) is True
    assert tracker.should_fire("error", 1.0) is False


def test_reset_allows_immediate_refire():
    tracker = DebounceTracker()
    assert tracker.should_fire("Error", 10.0) is True
    assert tracker.should_fire("Error", 10.0) is False
    tracker.reset()
    assert tracker.should_fire("Error", 10.0) is True
