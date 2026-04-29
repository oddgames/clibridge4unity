## CODE_EXEC log capture

`CODE_EXEC` and `CODE_EXEC_RETURN` now attach a `--- Logs (N) ---` block to their response with every `Debug.Log` / `Debug.LogWarning` / `Debug.LogError` line emitted during the run (up to 50 lines, any severity). Previously the auto-appended trailing block was errors-only, so user output from `Debug.Log` was invisible unless you ran a separate `LOG` command afterwards.

```
Code executed on main thread.
--- Logs (3) ---
[Log] starting…
[Warn] missing optional component
[Log] [Bridge] CODE_EXEC completed -> null
```

## Internal

- Added `LogCommands.GetLogsSinceAllFormatted(sinceId, maxLines)` — companion to the errors-only helper, surfaces all severities.
- `clibridge4unity.Commands.Code` asmdef now references `clibridge4unity.Commands.Core` so `LogCommands` is reachable from CodeExecutor.
