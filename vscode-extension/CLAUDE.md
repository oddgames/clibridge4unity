# clibridge4unity-vscode

A small VS Code extension that surfaces [`clibridge4unity`](https://github.com/) commands as status-bar buttons, so you can drive a running Unity Editor without leaving VS Code.

## What it does

`clibridge4unity` is a CLI that talks to the Unity Editor over a named pipe — it can trigger compiles, refresh assets, run tests, inspect the scene, etc. This extension is a thin GUI shim: it shells out to the CLI on button click and streams output back into a VS Code Output channel.

Today it exposes two buttons in the bottom-left status bar:

- **`$(zap) Unity: Compile`** — runs `clibridge4unity COMPILE`. Status bar shows a spinner while compiling, then flips to a check/error icon for a few seconds based on exit code. Full output streams into the **Unity Bridge** output channel.
- **`$(info) Unity: Status`** — runs `clibridge4unity STATUS`, parses the key/value report, and shows a severity-colored notification popup:
  - Red error if `hasCompileErrors` or `consoleErrors > 0`
  - Yellow warning if `compileRecommended` (popup includes a **Compile now** action that triggers the compile command)
  - Blue info for "compiling…", "main thread busy", or healthy ("Unity OK · scene: X · vN.N.N")
  - Every popup has a **Show details** button that reveals the full output channel.

## Architecture

One file, ~200 lines: [`src/extension.ts`](src/extension.ts).

- `activate()` creates the two `StatusBarItem`s and registers `clibridge.compile` / `clibridge.status` commands.
- `runBridge(channel, cmd, onClose)` is the shared spawn helper: resolves the executable + working directory from config, calls `child_process.spawn`, pipes stdout/stderr into the output channel, and buffers the output for callers that need to parse it.
- `resolveProjectPath()` picks the cwd in this order:
  1. `clibridge.projectPath` setting if non-empty
  2. First open workspace folder containing `ProjectSettings/ProjectVersion.txt`
  3. `undefined` (lets the CLI error with its own message)
- `showStatusNotification()` is the popup decision tree — reads parsed fields, picks severity, wires up action buttons.
- `parseStatus()` does a naive line-by-line `key: value` regex parse of the STATUS output.

## Settings

| Key | Default | Description |
|---|---|---|
| `clibridge.executablePath` | `"clibridge4unity"` | Path or PATH-resolvable name of the CLI. |
| `clibridge.projectPath` | `""` | Absolute path to the Unity project root. Empty = auto-detect from workspace folders. |

## Build / package / install

```bash
npm install            # one-time
npm run compile        # tsc -p ./ → out/extension.js
vsce package           # produces clibridge-X.Y.Z.vsix
code --install-extension clibridge-X.Y.Z.vsix --force
```

Reload the VS Code window after install (`Developer: Reload Window`).

## Dev loop

Open this repo in its own VS Code window and press **F5** — it launches an Extension Development Host with the extension loaded.

For testing against a real Unity project, either:
- Edit `.vscode/launch.json` to pass the Unity project path as a second arg in `"args"`, so the dev host opens with that workspace folder (auto-detection then kicks in), or
- Set `clibridge.projectPath` in the dev host's user settings.

## Adding new commands

The pattern for any clibridge command that's "run and show output" is trivial:

1. Add an entry to `contributes.commands` in `package.json`.
2. Optionally add a `StatusBarItem` in `activate()`.
3. Register a command handler that calls `runBridge(channel, "YOUR_CLIBRIDGE_VERB", onClose)`.

For commands whose output you want to parse and summarize (like STATUS), the buffered `output` string is passed to `onClose` — process it there.

## Constraints / gotchas

- `child_process.spawn` is called with `shell: true` so Windows can resolve the `.exe` from PATH. If the user sets `clibridge.executablePath` to a quoted path with spaces, it should still work, but untested.
- The COMPILE command sets a `compileRunning` flag to ignore concurrent clicks. STATUS does not — it's fast enough to allow overlap.
- The CLI's exit code is treated as the success signal. Non-zero exit on STATUS produces a generic "Is Unity running?" error popup.
- No file watchers, no auto-fire. Manual click only, by design.
