---
name: clibridge4unity-peers
description: Cross-window coordination when multiple Claude/CLI windows share ONE Unity editor. The CLI auto-detects other live windows and prints `[conflict] WARNING:` on stderr BEFORE a command that would stomp them (COMPILE/REFRESH break others' pipes, PLAY/STOP trample shared play mode, BUILD blocks everyone, same-asset edits clobber). This skill is how to read and respond to those warnings — the feature is passive/advisory, there is no "list peers" or "message peers" command. Auto-trigger on `[conflict] WARNING:`, "another window", "other Claude/CLI window", "COMPILE broke my pipe", "play mode changed by itself", "concurrent edit", "clobber", "last-writer-wins", `CLIBRIDGE_PEER_ID`, `CLIBRIDGE_BLOCK_ON_CONFLICT`, `.clibridge4unity/peers`, `PeerLedger`, "two windows one Unity", "shared editor".
---

# Cross-window coordination (peers)

One Unity editor is shared by every CLI/Claude window working on the project — one pipe server, one scene, one play-mode state, one asset database. That makes some actions destructive to *other* windows even when they look local to you. The CLI tracks a stable per-window identity plus recent activity and **warns before a command that would stomp another live window**.

**This is passive and advisory.** There is no command to run — you don't "list" or "message" peers. The CLI prints `[conflict] WARNING:` lines on **stderr** before risky commands; your job is to read them and decide. It never blocks (exit code is unchanged) unless you opt in (see below).

## What collides, and why

- **`COMPILE` / `REFRESH`** domain-reload Unity → **break every other window's in-flight pipe command** and reset the shared play mode.
- **`PLAY` / `STOP` / `PAUSE` / `STEP`** change the **shared** play state for everyone.
- **`BUILD`** blocks **all** bridge commands for every window until it finishes.
- **Two windows editing the same asset/file** → silent last-writer-wins clobber.

## How the warning works

Before each command the CLI records this window's activity (`{id}.peer` / `{id}.active`), checks the other live windows, and prints any `[conflict] WARNING: …` to stderr. The cases it warns on:

1. **A peer is mid `COMPILE`/`REFRESH` right now** → your command will likely time out; wait for their reload.
2. **A peer is mid `BUILD` right now** → Unity rejects all commands during a build; yours will bounce until it ends.
3. **You're about to `COMPILE`/`REFRESH`** → you'll break that peer's in-flight command and reset play mode.
4. **You're about to `PLAY`/`STOP`/`PAUSE`/`STEP`** and a peer is in play mode → you change their play state too.
5. **You're about to `BUILD`** → you lock every other window out until it finishes.
6. **Same-asset write** (`ASSET_MOVE`/`COPY`/`DELETE`/`RESERIALIZE`/`LABEL`/`MKDIR`, `PREFAB_CREATE`/`PREFAB_SAVE`, `SAVE`/`LOAD`) where a peer touched an overlapping path in the last ~10 min → possible clobber.

**Edit detection beyond the CLI:** a PreToolUse hook (matcher `Edit|Write|MultiEdit|NotebookEdit|Grep`) records file edits per-window, so concurrent edits to the *same source file* are caught — not just CLI asset commands. Set `CLIBRIDGE_BLOCK_ON_CONFLICT=1` to make that edit hook **block** (exit 2) on a fresh conflict instead of merely advising.

## How to respond to `[conflict] WARNING:`

- **Peer is recompiling / building** → don't retry in a loop; wait for their reload/build to finish, then go. Use `STATUS`/`DIAG` (always answer, never stomp) to check whether Unity is free again.
- **You're the one about to COMPILE/REFRESH/BUILD/PLAY** → that's destructive to them. Defer until their in-flight command completes, or coordinate before stomping shared state.
- **Concurrent-edit warning** → don't overwrite a file another window touched minutes ago. Confirm before you clobber; re-read the file first.

The warnings are deliberately advisory so they never wedge automation — but treat them as real. Blindly re-running a command that just warned is how two windows deadlock each other.

## Identity

- Each window gets a stable id like `peer-3F2A`, anchored to the parent `claude`/terminal process (the CLI invocation itself is ephemeral — one process per command — so it can't anchor identity). The id survives across invocations.
- **Two chat panels hosted by one process** (e.g. two panels in the same VSCode window) collapse to one id. Set `CLIBRIDGE_PEER_ID=<something-unique>` in each to disambiguate. The override also pins a guaranteed-stable id for harness/CI use.
- Liveness is by anchor-PID: when the anchoring window process is gone, that peer's records are pruned on the next read.

## Storage

`{project}/.clibridge4unity/peers/`:
- `{id}.peer` — durable presence: last-seen, cwd, play-mode flag, an activity ring (last ~8 meaningful commands) and a touched-paths ring (last ~12 asset paths).
- `{id}.active` — present **only while a command is in flight** (the "right now" signal); cleared when the command returns, and treated as stale if the invocation PID is dead.

These are plain key=value text files; nothing here needs hand-editing — it's the CLI's coordination state.

## Related
- `clibridge4unity-bridge` — the command surface these warnings guard
- `clibridge4unity-lint` — `COMPILE` discipline; the most common cross-window stomp
- `clibridge4unity-build` — `BUILD` blocks every window until it finishes
- `clibridge4unity-assets` — asset-write commands that trigger same-file clobber warnings
