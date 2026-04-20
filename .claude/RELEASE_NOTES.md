Release-notes + CODE_EXEC line numbers.

### Release notes pipeline
- `deploy.py` now builds release notes from `.claude/RELEASE_NOTES.md` (cleared after use) or synthesises from `git log <prev>..<tag>` + `git diff --shortstat` + a compare link, then passes via `--notes-file` to `gh release create`. Replaces the prior `--generate-notes` which produced nothing useful on a PR-less repo.
- Retroactively filled proper notes for **v1.0.57 → v1.0.68** via `backfill_release_notes.py` (all 12 sessions' worth of changes are now on the releases page).

### CODE_EXEC stack traces now point at your line
- Roslyn backend emits a PDB alongside the PE. Syntax tree parses with `path: "Script.cs"` and `encoding: UTF-8` — both required, otherwise Roslyn refuses with `CS8055`.
- Assembly loaded via `Assembly.Load(peBytes, pdbBytes)` so the CLR resolves sequence points at runtime.
- mcs fallback: `CompilerParameters.IncludeDebugInformation = true` and `-debug:full`.
- Runtime exceptions in `CODE_EXEC` / `CODE_EXEC_RETURN` scripts now look like `NullReferenceException ... at Runner.Run () in Script.cs:5` instead of a bare IL offset.
