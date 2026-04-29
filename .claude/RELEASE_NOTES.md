## Heartbeat-aware bail / inline-wait

The CLI now uses Unity's heartbeat file to decide whether to wait or exit fast when Unity is busy:

- **Heartbeat says busy + est_remaining > 7s** → exits immediately with `busy: <state> (~Ns remaining)` and a retry hint. No more 60s blind waits.
- **est_remaining ≤ 7s** → CLI sleeps that long inline, then sends the command with a tighter read deadline (the wait eats from the same budget — total time stays roughly constant).
- **elapsed > avg × 2** → reports Unity as wedged instead of waiting indefinitely.

Bypass list (commands that legitimately want to talk to a busy Unity): `STATUS`, `DIAG`, `PROBE`, `PING`, `LOG`, `HELP`, `COMPILE`, `REFRESH`, `WAKEUP`, `DISMISS`. Everything else gets the bail-fast / inline-wait treatment.

## Heartbeat now records `stateEnteredAt`

`Heartbeat.cs` writes the unix timestamp of when Unity entered its current state, so the CLI can compute elapsed-in-state and produce accurate ETAs. Existing fields (`state`, `compileTimeAvg`, etc.) unchanged.

## CODE_EXEC log capture (carry-over from v1.0.74)

`CODE_EXEC` and `CODE_EXEC_RETURN` attach a `--- Logs (N) ---` block to their response with all severities — `Debug.Log` output is now visible without a separate `LOG` command.
