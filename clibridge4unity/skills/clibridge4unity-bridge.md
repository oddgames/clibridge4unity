---
name: clibridge4unity-bridge
description: Orientation for clibridge4unity — when to use it, how to connect, how to choose between the per-task skills. Trigger when the user asks anything about Unity Editor automation, running commands against a Unity project, or first-time clibridge4unity use.
---

# Unity Bridge orientation

`clibridge4unity` is a CLI that drives the Unity Editor over a named pipe. Use it whenever the user needs to make Unity do something — inspect the scene, run code, take a screenshot, run tests, build the player, etc. — instead of asking them to click through the Editor UI.

## When to use which skill

| Task | Skill |
|---|---|
| Run C# inside Unity (one-off snippet or get a value back) | `unity-run-code` |
| Search/inspect code (definitions, usages, derived types) | `unity-find-code` |
| Diagnose a suspected compile error | `unity-lint` |
| Manipulate GameObjects / scene / play mode | `unity-scene` |
| Add/remove/configure components on a GameObject | `unity-components` |
| Create or modify prefab assets | `unity-prefab` |
| Search/move/copy/delete project assets | `unity-assets` |
| Capture a screenshot (editor, gameview, prefab, UXML, GameObject) | `unity-screenshot` |
| Drive the running player via clicks/typing/swipes | `unity-ui-automation` |
| Run EditMode / PlayMode tests | `unity-tests` |
| Build a standalone player | `unity-build` |

The other commands (`PING`, `STATUS`, `DIAG`, `PROBE`, `LOG`, `WAKEUP`, `DISMISS`, `MENU`, `PROFILE`) are general-purpose — covered below.

## Project detection & connection

The CLI auto-detects the Unity project by walking up from the current directory looking for `Assets/`. Override with `-d`:

```bash
clibridge4unity PING                     # auto-detect from cwd
clibridge4unity -d /path/to/project PING # explicit
```

Use `clibridge4unity -h` to list every command the connected Unity instance currently exposes — it's authoritative; this skill is curated.

## Editor state — read it cheaply before reacting

- `STATUS` — compile state, play mode, errors. The cheap first check.
- `DIAG` — works even when Unity's main thread is blocked. Use when commands time out.
- `PROBE` — quick main-thread liveness check (~2s).
- `LOG errors` — current console errors. `LOG errors verbose` for stacks.

**Don't run `COMPILE` or `LINT` reactively after every edit.** Unity auto-recompiles when it gains focus; `STATUS` is the right check. See `unity-lint` for when those commands earn their keep.

## Output slicing — use LAST, not `| head`

Every command's full stdout is cached (last 10). Re-running a command to "just see the top" wastes a Unity round-trip and can race with imports.

```bash
clibridge4unity LAST          # full text of most recent response
clibridge4unity LAST -head 50 # first 50 lines of most recent
clibridge4unity LAST 2 -tail 30
clibridge4unity LAST -grep ERROR
clibridge4unity LAST -list    # table of all 10 cached responses
```

## When Unity is unresponsive

1. `DIAG` — always answers, tells you whether Unity is compiling/importing/dialog-blocked.
2. `LOG errors` — what's broken.
3. `WAKEUP` — bring Unity to the foreground if it's been backgrounded (its message pump may have stalled).
4. `DISMISS` — close modal dialogs that are blocking the main thread.

## Multi-instance

If multiple Unity Editors are open, the pipe name is per-project (`UnityBridge_{User}_{ProjectHash}`). Use `-d` to target the right project explicitly; otherwise the CLI uses cwd.
