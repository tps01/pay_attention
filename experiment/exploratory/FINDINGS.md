# Exploratory defaults testing

Branch: `experiment/exploratory-defaults-testing` (merged into develop after fault fixes)

## Post-fix verification (selected scenarios)

| Scenario | Result | Notes |
| --- | --- | --- |
| plain_fast_threshold_70 | PASS | Idle Fast now runs full cascade |
| all_monitors_scope | PASS | Per-monitor cascade budget |
| blacklist_edge_should_skip | PASS | DWM mask + overlap logging |

See earlier table in git history for the initial 11/15 run. Product fixes from that run are in CaptureMonitor / ScreenGrabber / docs.

## Cleanup

```powershell
Remove-Item -Recurse -Force experiment\exploratory\captures_runs, experiment\exploratory\edge-profile, experiment\exploratory\logs -ErrorAction SilentlyContinue
```
