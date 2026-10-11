# Exploratory defaults testing

Experimental branch only (`experiment/exploratory-defaults-testing`).

- Opens **Edge** on the **C43J89x** ultrawide (`3840×1200` at `-3840,0`).
- Drives `CaptureMonitor` with many Settings combinations.
- Writes captures/logs/configs **only** under this folder (not `%LocalAppData%\pay_attention`).

See [FINDINGS.md](FINDINGS.md) for results and default recommendations.

```powershell
dotnet run --project experiment\exploratory\ExploreRunner\ExploreRunner.csproj -c Release
```
