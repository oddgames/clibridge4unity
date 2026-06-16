---
name: clibridge4unity-build
description: Build the Unity standalone player through clibridge4unity. Use when the task is "build the player", "make a standalone/exe/apk", "build and run", or diagnosing a failed BuildPipeline run. Covers BUILD --run/--dev/--output, active-target builds, and reading build-failure logs.
---

# Build

Standard Unity BuildPipeline/Build Settings knowledge applies — below is only what's specific to this CLI.

`BUILD` runs `BuildPipeline.BuildPlayer` on the **active build target** (set in Unity, not by a flag) and streams progress.

```bash
clibridge4unity BUILD                       # active target, default output
clibridge4unity BUILD --run                 # launch player after success (Standalone only)
clibridge4unity BUILD --dev                 # Development | AllowDebugging
clibridge4unity BUILD --output <path>       # custom output (absolute or relative to project)
```

- **Scenes** — only **enabled** entries in `EditorBuildSettings.scenes`. Zero enabled = immediate error.
- **Default output** — `Builds/<Target>/<ProductName>` under project root. `--output` overrides.
- **Output stream** — header (`Target`, `Scenes`, `Output`, `Build started...`), then `[err] …` / `[warn] …` during build, then summary (`Result`, `Duration`, `Size`, `Warnings`/`Errors`, final `Output`). Server timeout 1800s.

## --run

Launches **Standalone** (Win/Mac/Linux) only. Otherwise prints a hint instead:
- **Android** — `adb install -r "<path>"` then `adb shell am start <package>`
- **WebGL** — serve the build dir (`python -m http.server`)

## Auto-block during a build

All non-diagnostic commands return a clear error immediately (no hung pipe). Pass-through: `PING`, `DIAG`, `STATUS`, `PROBE`, `HELP`, `LOG`.

## Diagnosing a failed build

The trailing `BUILD FAILED` / summary line is **not** the cause. The real error is the first upstream `[err]` line, typically a `[Preprocess Player]` or `[Packaging assets]` step (shader/addressables/asset-bundle errors surface here).

```bash
clibridge4unity LAST -grep "\[err\]"   # re-read error lines from cached run
clibridge4unity LAST -head 40          # header + first failures
clibridge4unity STATUS                 # confirm compile is clean first
```

A first build to a new target can trigger compilation + assembly reload, dropping the pipe — reconnect and re-check with `STATUS`.

## Related

- `clibridge4unity-bridge` — orientation, connection, editor state
- `clibridge4unity-tests` — verify before building
- `clibridge4unity-shaders` — shader errors at build/preprocess time
