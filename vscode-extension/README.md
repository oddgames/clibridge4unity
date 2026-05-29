# Unity clibridge

A small VS Code extension that surfaces [`clibridge4unity`](https://github.com/oddgames/clibridge4unity) commands as status-bar buttons, so you can drive a running Unity Editor without leaving your editor.

`clibridge4unity` is a CLI that talks to the Unity Editor over a named pipe — it can trigger compiles, refresh assets, run tests, inspect the scene, and more. This extension is a thin GUI shim: it shells out to the CLI on a button click and streams output back into a VS Code Output channel.

## Buttons

Two buttons appear in the bottom-left status bar:

- **`$(zap) Unity: Compile`** — runs `clibridge4unity COMPILE`. A spinner shows while compiling, then a check/error icon for a few seconds based on exit code. Full output streams into the **Unity Bridge** output channel.
- **`$(info) Unity: Status`** — runs `clibridge4unity STATUS`, parses the report, and shows a severity-colored notification:
  - Red error if there are compile or console errors
  - Yellow warning if a compile is recommended (with a **Compile now** action)
  - Blue info for "compiling…", "main thread busy", or healthy (`Unity OK · scene: X · vN.N.N`)
  - Every popup has a **Show details** button that reveals the full output channel.

## Install

The easiest path is the CLI itself — from a Unity project that has clibridge4unity installed, run:

```
clibridge4unity VSCODE
```

This installs the version of the extension that exactly matches your CLI (the `.vsix` is bundled inside the CLI), then reload the editor window (`Developer: Reload Window`) to activate it.

You can also install a packaged `.vsix` manually:

```
code --install-extension clibridge-<version>.vsix --force
```

## Settings

| Key | Default | Description |
|---|---|---|
| `clibridge.executablePath` | `"clibridge4unity"` | Path or PATH-resolvable name of the CLI. |
| `clibridge.projectPath` | `""` | Absolute path to the Unity project root. Empty = auto-detect from open workspace folders. |

## Requirements

- The `clibridge4unity` CLI must be installed and on your `PATH` (or set `clibridge.executablePath`).
- A running Unity Editor with the clibridge4unity package, for the commands to do anything.

See [`CLAUDE.md`](CLAUDE.md) for the extension's architecture and development guide.
