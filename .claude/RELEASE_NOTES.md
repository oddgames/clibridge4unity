## KILL command + auto-kill flag

When Unity wedges (heartbeat says busy but `elapsed > avg × 2`), you can now recover without going through Task Manager.

- **`clibridge4unity KILL`** — CLI-side, no pipe needed. Force-terminates every Unity process associated with the targeted project. Reports how many were killed and points you at `OPEN` to relaunch. Loses unsaved scene/prefab edits — warning shown.
- **`--kill-if-wedged` flag** — opt-in, applies to any pipe-bound command. If the heartbeat-aware pre-wait detects "wedged", the CLI auto-terminates Unity instead of bailing with a "possibly wedged" message. Same caveat: unsaved work is lost.

The non-flag wedge message now also points users at `KILL` as the manual escape hatch.
