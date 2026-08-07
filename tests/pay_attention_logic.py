"""Mirrored contracts of PayAttention.Core logic for pytest.

Source of truth:
- src/PayAttention.Core/Services/TriggerMatcher.cs
- src/PayAttention.Core/Services/DebounceTracker.cs
- src/PayAttention.Core/Models/AppConfig.cs
- src/PayAttention.Core/Services/ConfigService.cs
"""

from __future__ import annotations

import re
import time
from dataclasses import dataclass, field
from typing import Iterable, Literal, Optional

MatchMode = Literal["contains", "exact", "regex"]
CaptureAction = Literal["screenshot", "record", "sequence"]

# Defaults mirrored from AppConfig / ConfigService.Normalize / CreateDefault
START_MONITORING_ON_LAUNCH_DEFAULT = False
DEBUG_SAVE_JSON_SIDECARS_DEFAULT = False
OCR_MAX_WIDTH_DEFAULT = 1920
OCR_MAX_WIDTH_MIN = 1600
OCR_MAX_WIDTH_MAX = 3840
CAPTURE_SCOPE_DEFAULT = "monitor"
OCR_INVERT_DEFAULT = False
OCR_UPSCALE_DEFAULT = 100
OCR_UPSCALE_MIN = 100
OCR_UPSCALE_MAX = 400
RECORD_FORMAT_DEFAULT = "avi"
RECORD_JPEG_QUALITY_DEFAULT = 70
RECORD_JPEG_QUALITY_MIN = 40
RECORD_JPEG_QUALITY_MAX = 95
SCREENSHOT_FORMAT_DEFAULT = "jpeg"
SEQUENCE_INTERVAL_DEFAULT = 3
SEQUENCE_INTERVAL_MIN = 1
SEQUENCE_INTERVAL_MAX = 30
SEQUENCE_BEFORE_DEFAULT = 60
SEQUENCE_AFTER_DEFAULT = 60
SEQUENCE_BEFORE_AFTER_MIN = 0
SEQUENCE_BEFORE_AFTER_MAX = 60
SAMPLE_TRIGGERS_DEFAULT = (
    {"text": "Error", "match": "contains", "action": "screenshot", "enabled": False},
    {"text": "Exception", "match": "contains", "action": "record", "enabled": False},
)


@dataclass
class TriggerConfig:
    text: str
    match: MatchMode = "contains"
    action: CaptureAction = "screenshot"
    enabled: bool = False
    id: str = ""


@dataclass
class TriggerMatchResult:
    trigger: TriggerConfig
    matched_portion: str


def _normalize(text: str) -> str:
    # Mirror TriggerMatcher: OCR often turns underscores into spaces.
    cleaned = text.replace("\u200b", "").replace("\ufeff", "").replace("\u00a0", " ")
    return re.sub(r"[\s_]+", " ", cleaned).strip()


def _fold_confusables(text: str) -> str:
    """Mirror TriggerMatcher.FoldConfusables — punctuation becomes separators."""
    mapping = {
        "0": "0", "O": "0", "o": "0", "\u039f": "0", "\u03bf": "0",  # Greek Omicron
        "1": "1", "l": "1", "I": "1", "i": "1", "|": "1", "\u00a1": "1",  # ¡
        "2": "2", "Z": "2", "z": "2",
        "3": "9", "9": "9", "g": "9",
        "5": "5", "S": "5", "s": "5",
        "8": "8", "B": "8",
        "6": "6", "G": "6",
        "A": "a", "a": "a", "\u0391": "a",  # Greek Alpha
        "E": "e", "e": "e", "\u20ac": "e",  # €
        "Q": "q", "q": "q",
        "X": "x", "x": "x", "\u00d7": "x",  # ×
        "U": "u", "u": "u", "V": "u", "v": "u",
    }
    out = []
    for ch in text:
        if ch in mapping:
            out.append(mapping[ch])
        elif ch.isalnum():
            out.append(ch.lower())
        else:
            out.append(" ")
    return re.sub(r"\s+", " ", "".join(out)).strip()


def _compact_alnum(folded: str) -> str:
    return re.sub(r"[^0-9a-z]+", "", folded, flags=re.IGNORECASE)


def _levenshtein(a: str, b: str) -> int:
    n, m = len(a), len(b)
    d = [[0] * (m + 1) for _ in range(n + 1)]
    for i in range(n + 1):
        d[i][0] = i
    for j in range(m + 1):
        d[0][j] = j
    for i in range(1, n + 1):
        for j in range(1, m + 1):
            cost = 0 if a[i - 1] == b[j - 1] else 1
            d[i][j] = min(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + cost)
    return d[n][m]


def _fuzzy_contains(haystack: str, needle: str) -> bool:
    if not needle or len(needle) < 6:
        return False
    if needle in haystack:
        return True
    max_distance = 2 if len(needle) >= 8 else 1
    if len(haystack) < len(needle) - max_distance:
        return False
    min_window = max(1, len(needle) - max_distance)
    max_window = len(needle) + max_distance
    for win in range(min_window, max_window + 1):
        if len(haystack) < win:
            break
        for i in range(0, len(haystack) - win + 1):
            if _levenshtein(haystack[i : i + win], needle) <= max_distance:
                return True
    return False


def find_match(ocr_text: str, triggers: Iterable[TriggerConfig]) -> Optional[TriggerMatchResult]:
    """Mirror of TriggerMatcher.FindMatch (first match)."""
    matches = find_matches(ocr_text, triggers)
    return matches[0] if matches else None


def find_matches(ocr_text: str, triggers: Iterable[TriggerConfig]) -> list[TriggerMatchResult]:
    """Mirror of TriggerMatcher.FindMatches."""
    if not ocr_text or not ocr_text.strip():
        return []

    normalized_haystack = _normalize(ocr_text)
    folded_haystack = _fold_confusables(normalized_haystack)
    compact_haystack = _compact_alnum(folded_haystack)
    results: list[TriggerMatchResult] = []

    for trigger in triggers:
        if not trigger.enabled or not (trigger.text or "").strip():
            continue

        if trigger.match == "exact":
            needle = _normalize(trigger.text)
            folded_needle = _fold_confusables(needle)
            if (
                normalized_haystack.casefold() == needle.casefold()
                or folded_haystack == folded_needle
                or compact_haystack == _compact_alnum(folded_needle)
            ):
                results.append(TriggerMatchResult(trigger=trigger, matched_portion=trigger.text))
        elif trigger.match == "regex":
            try:
                m = re.search(trigger.text, ocr_text, flags=re.IGNORECASE)
            except re.error:
                continue
            if m:
                results.append(TriggerMatchResult(trigger=trigger, matched_portion=m.group(0)))
        else:  # contains
            needle = _normalize(trigger.text)
            folded_needle = _fold_confusables(needle)
            compact_needle = _compact_alnum(folded_needle)
            if (
                needle.casefold() in normalized_haystack.casefold()
                or folded_needle in folded_haystack
                or (compact_needle and compact_needle in compact_haystack)
                or _fuzzy_contains(compact_haystack, compact_needle)
            ):
                results.append(TriggerMatchResult(trigger=trigger, matched_portion=trigger.text))

    return results


@dataclass
class DebounceTracker:
    """Mirror of DebounceTracker (wall-clock based)."""

    _last_fired: dict[str, float] = field(default_factory=dict)

    def should_fire(self, key: str, debounce_seconds: float) -> bool:
        now = time.time()
        last = self._last_fired.get(key.casefold())
        if last is not None and (now - last) < debounce_seconds:
            return False
        self._last_fired[key.casefold()] = now
        return True

    def reset(self) -> None:
        self._last_fired.clear()


def clamp_ocr_max_width(value: int) -> int:
    """Mirror of ConfigService.Normalize OcrMaxWidth clamp."""
    return max(OCR_MAX_WIDTH_MIN, min(OCR_MAX_WIDTH_MAX, value))


def clamp_ocr_upscale(value: int) -> int:
    """Mirror of ConfigService.Normalize OcrUpscalePercent clamp."""
    return max(OCR_UPSCALE_MIN, min(OCR_UPSCALE_MAX, value))


def clamp_record_jpeg_quality(value: int) -> int:
    """Mirror of ConfigService.Normalize RecordJpegQuality clamp."""
    return max(RECORD_JPEG_QUALITY_MIN, min(RECORD_JPEG_QUALITY_MAX, value))


def clamp_sequence_interval(value: int) -> int:
    return max(SEQUENCE_INTERVAL_MIN, min(SEQUENCE_INTERVAL_MAX, value))


def clamp_sequence_before_after(value: int) -> int:
    return max(SEQUENCE_BEFORE_AFTER_MIN, min(SEQUENCE_BEFORE_AFTER_MAX, value))
