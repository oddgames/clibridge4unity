## Settle-wait before pipe commands

Unity sometimes chains domain reloads — script-edit compile → reload, then a second forced sync recompile right after (post-import housekeeping or another package's `[InitializeOnLoad]`). The heartbeat flips to `ready` for a moment in the gap. If a CLI command landed in that window, the pipe call would die mid-reload.

`HeartbeatAwarePreWait` now requires `state=ready` to have been stable for ≥2s. If the heartbeat just transitioned to ready, the CLI sleeps the remainder before sending. The wait is subtracted from the read budget, same as the busy-wait path — total time stays roughly constant.

## UPDATE auto-recovery for stale .old binary

Previous behaviour: if `clibridge4unity.exe.old` was held by a stale CLI process, `UPDATE` failed with `UnauthorizedAccessException` and pointed at the install script.

Now: on lock, `UPDATE` kills any *other* clibridge4unity processes (excluding self), retries the delete, and proceeds. If it still fails, the error message prints the exact PowerShell recovery commands inline:

```powershell
Get-Process clibridge4unity -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item "$env:USERPROFILE\.clibridge4unity\clibridge4unity.exe.old" -Force -ErrorAction SilentlyContinue
clibridge4unity UPDATE
```
