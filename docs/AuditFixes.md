# Correctness audit fixes — 1.5.3

This ledger addresses the 24 findings from the 2026-10-18 audit of develop
`1aa53468c9171a8419bae3924c6e033859f393bd` (1.5.2). It describes the implementation
and its validation limits; it is not a claim that every desktop workload was qualified.

| Finding | Result | Regression evidence |
| --- | --- | --- |
| PA01 Retention deletes unrelated files | Reserved `pa_<timestamp>_<event-guid>_<label>_<action>` ownership namespace; companions form one event; active leases and reparse trees are skipped. Legacy files remain untouched. Maintenance runs independently of capture/pause. | Owned, foreign, legacy and active-event retention tests. |
| PA02 Isolated Save/Import touches autostart | Startup, Save and Import share the isolation-aware `ConfigService.ApplyAutostart` policy. | Injected registry writer remains uncalled for isolated Save/Import. |
| PA03 Sequence continuation bypasses capture policy | All history and continuation use the same eligible, blacklist-masked producer; no sequence-specific screen grab. | Pipeline test checks pixels in every sequence image. |
| PA04 Pause leaves queued OCR active | Epoch invalidation drops stale OCR results, clears pending frames/history and prevents sequence continuation. Resume admits the same scene again. Already committed file operations may settle. | Pause during blocked OCR, resume, interrupted-sequence receipt and no later appends. |
| PA05 OCR and saved image disagree | Every pass reads the frozen evidence bitmap (or a crop of it), never a later live HWND grab. | Delayed OCR matches blue pixels while the producer moves to red; saved image remains blue. |
| PA06 Foreground failure expands scope | Failed foreground grab skips the sample without monitor fallback. | Null foreground capture test. |
| PA07 Semantic config corruption | Validate enum domains, null rows and unique trigger/application IDs on load/import/save. Invalid existing files remain intact, with a warning and no automatic monitoring or autostart change based on fallback defaults. | Invalid JSON values, null elements, duplicate IDs, preservation and nonactivation tests. |
| PA08 Scene changes disappear or are sampled too late | Quantized content hashes replace the coarse 8×8 average hash in monitoring; the producer honors OCR cadence without a five-interval quiet throttle. | Realistic terminal-line change test; cadence pipeline test. |
| PA09 Large/many windows lose OCR coverage | Frozen whole-frame OCR removes the live per-window eligibility filter, 12-window cap and partial-result shortcut. The legacy `ocrMaxWindows` setting remains readable but is hidden in Settings. | Frozen-image pipeline tests; live whitelist fixture. Full multi-window/monitor qualification remains environment-dependent. |
| PA10 Unbounded, untracked writers | Maximum four admitted writers; clone after admission; guaranteed disposal even when stopping. Async Stop/Restart/Exit await pipeline and writes. Rejected admissions schedule retry. Harness finish callbacks marshal to their UI thread before stopping. | Blocked committed writer and bounded-writer/retry pipeline tests. |
| PA11 Failed save suppresses retries | Fire gate reserves pending claims; only successful persistence commits debounce/scene state. Failure abandons its own claim and schedules resampling. | Repair a failed output path without changing the scene; stale-claim isolation test. |
| PA12 Incorrect pre-roll/timeline | Capture-time history snapshots exclude future frames. Recording sampling can exceed OCR cadence. AVI supports fractional FPS based on actual timestamps; GIF retains timestamp delays. | Historical boundaries, sparse AVI time-base and independent record-cadence tests. |
| PA13 Late sequence catch-up is misleading | Consume timestamped real samples at the configured spacing; recover available historical post-frames after slow OCR; enforce event-relative end time; keep fractional offsets distinct. Interrupted receipts say incomplete. | Selection/deadline, slow OCR recovery and pause tests. |
| PA14 Filename collisions | Unique event GUID per output, independent of sanitized/truncated label. | Same-frame sanitized collision regression. |
| PA15 Fast threshold is bypassed | Fast no longer takes the unconditional Thorough fuzzy path. Its configured percentage controls approximate matches after normalization. Similarity pruning preserves better scores across different window lengths. | 100/90/80/70 threshold examples and unpruned edit-distance oracle. |
| PA16 Unrelated Unicode exact matches | Preserve Unicode letters/digits and reject empty compact/folded fallback needles. | Chinese and punctuation-only exact cases. |
| PA17 Calibration destroys form edits | Start from the current form and merge only OCR/profile fields into the latest form snapshot after benchmarking, including tray invocation. | Unsaved triggers, applications, output folder, debounce and startup settings survive calibration merge. |
| PA18 Settings round-trip proves nothing changed | Actually edit debounce 30 → 17; poll persisted value; reopen and verify the field value. | Revised FlaUI scenario; requires an interactive desktop. |
| PA19 Empty/partial output passes | Require completion receipt, exact trigger identity and decoded media. Harness walks AVI chunks and decodes JPEG frames. Positive acceptance additionally OCRs the saved trigger still through the production cascade. | Empty/corrupt/pending/missing-receipt, truncated AVI, wrong-trigger and failed-content-proof regressions; live screenshot/record case. |
| PA20 Debounce/pause scenarios are inconclusive | Debounce changes pixels but retains the same trigger ID and monitor session, then proves expiry. Pause/resume baselines precede the actions, wait for logged acknowledgement and require the expected trigger ID. | Revised live scenarios; pipeline pause/retry tests. |
| PA21 Pairwise dimensions are correlated | Ten rows now cover every binary factor pair, including fast/single and slow/multi. Parser accepts all rows. Easy font choice remains independent of scrolling, including the dmesg recipe. | Offline covering-array check; live-case results below. |
| PA22 Evidence disappears at teardown | Archive config/media/app logs before cleanup. Harness preserves its own workspace and archives on completion; Python saves stdout/stderr and recovers printed workspaces on timeout. Copy failure preserves originals. | Archive success/failure regressions; retained live-run evidence. |
| PA23 Verify-only reruns side effects | Reject unsupported `verify_only` at every script entrance and mutating helper boundary. Cache a successful solution build per suite process. Harness executable launches have bounded waits. | Every script entry rejects verify-only before desktop actions; build caching test. |
| PA24 Releases have no unit gate | Run .NET tests before publishing a tagged release. PRs to develop and develop pushes build/test the solution and run offline Python helper regressions. | Workflow definitions; local build/test commands below. |

## Behavior and trade-offs

- Retention intentionally does **not** infer ownership of old filenames. Existing 1.5.2
  captures and unrelated files need manual cleanup. A stale `.pending` marker left by
  a crashed process also requires inspection rather than automatic deletion.
- Frozen whole-frame recognition prioritizes scope/evidence consistency. Large frames
  may cost more OCR time than the previous live per-window shortcut. Optimize-for-this-PC
  remains a small-fixture heuristic, not a full-pipeline performance guarantee.
- Application association/prep/debounce follow the frame's foreground or sticky listed
  application; OCR does not attribute each phrase to a particular visible window.
- Sequence history retains before + after + 60 seconds to tolerate OCR lag. Longer lag
  can leave gaps. Recording FPS is a target, and fractional AVI FPS approximates irregular
  intervals rather than providing a variable-frame-rate container. Resolution changes
  during AVI history still omit frames whose dimensions differ from the first frame.
- Completion receipts contain IDs, paths, action and state, not OCR text. Optional debug
  sidecars and Debug logs can contain text. Saved-image proof checks the recording's
  paired trigger still; it does not assert that every video frame contains the phrase.
- The tray now offers Pause/Resume. The guide now correctly names Fast as the trigger
  default and explains failed foreground capture, pause/shutdown, receipts and retention.

## Validation

```powershell
dotnet build PayAttention.sln -c Release --no-restore
dotnet test tests/PayAttention.Tests/PayAttention.Tests.csproj -c Release --no-build
python -m pytest tests/python -q
```

The .NET suite has 174 passing tests; offline Python helper tests have 8 passing tests.
Live pairwise cases 1 and 9 passed with decoded screenshot/AVI and saved-still OCR verification.
Case 9 detected the fast single-pane screenshot/record pair in 2447/2450 ms, within
the existing 15000 ms budget. Its initial failure exposed that the old recipe ignored
the Easy font setting; difficulty now remains independent of scrolling.
Evidence is retained locally under `artifacts/live-audit` (not committed).

Monitor-scope live case 10 encountered `Graphics.CopyFromScreen` / Win32 error 6
(invalid handle) in this execution desktop. This prevents a truthful full interactive
suite qualification here. Such failures remain failures, not skipped or passing results.
The full FlaUI/settings/hotkey and monitor matrix must be run in an unlocked desktop
before claiming complete interactive coverage. See `tests/colosseum/README.md` for the
live suite and evidence paths.
