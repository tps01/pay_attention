# AGENTS.md

Baseline expectations for AI agents in this repository.

## Purpose

Windows tray OCR capture app (C# / .NET 8). Local only; no cloud. Treat captures
and OCR logs as potentially sensitive.

Do not commit, push, merge, or tag unless the user explicitly requests it.

## Soft branch naming

Prefer short-lived branches off `develop`:

- `feature/<short-name>` — new behavior
- `bugfix/<short-name>` — defect fixes aimed at `develop`
- `hotfix/<short-name>` — urgent fixes that may target `main` (then back-merge to
  `develop`)

Naming is guidance, not a hard gate. Do not invent long-lived feature branches.
**Never delete `develop` or `main`.**

## GitHub workflow

- Day-to-day: branch → PR into **`develop`** → wait for CI (`Tests` / job `test`)
  → merge when green. Prefer merge or squash; GitHub
  `delete_branch_on_merge` removes **feature/bugfix/hotfix** heads only.
- Do not push straight to `develop` or `main` for non-trivial work; both are
  protected (PR required, required status checks, no force-push / branch
  deletion).
- Release: when asked, open **`develop` → `main`**, merge when green, then tag
  `vX.Y.Z` and `git push origin vX.Y.Z` (Release workflow builds the win-x64 zip).
  After the release merge, confirm `origin/develop` still exists; if
  `delete_branch_on_merge` removed it, recreate with
  `git push origin origin/main:refs/heads/develop`. Do not treat head-branch
  deletion as success for release PRs.
- Keep PRs focused; one coherent concern when practical.
- Commit subjects: short imperative (e.g. `Fix pause hotkey race on Stop.`).
- PR body: behavior, how tested, limitations; screenshots for UI changes.

### Useful `gh` commands

- `gh pr create`, `gh pr list`, `gh pr view`, `gh pr checks`, `gh pr merge`
- `gh run list`, `gh run view`, `gh run rerun` (and `--failed`)
- `gh release list` / `gh release view` after tags land

## Commands

```powershell
dotnet build PayAttention.sln -c Release
dotnet run --project src/PayAttention -c Release
dotnet test tests/PayAttention.Tests/PayAttention.Tests.csproj -c Release
.\scripts\publish.ps1
```

Optional version for About / assembly: `.\scripts\publish.ps1 -Version 1.5.3`

## Testing

- Always run unit tests before claiming done: `dotnet test …PayAttention.Tests…`.
- Desktop / Colosseum suite tests need Windows, an interactive session, and
  uncovered harness windows. See `tests/colosseum/README.md`. Do not claim those
  passed without running them (or note they were skipped).
- When a test fails, reproduce before fixing. Do not weaken or delete tests just
  to get green.

## Security & artifacts

- Never commit captures, OCR sidecars, local config, tokens, or log dumps.
- `%LocalAppData%\pay_attention\` (config, captures, logs) stays off git.
- Sanitize evidence before sharing; logs may contain trigger names and OCR text.

## Change discipline

Keep changes focused. Match surrounding C# style. Prefer the smallest fix that
works. Update docs/tests with behavior changes.
