---
name: clibridge4unity-run-code
description: Execute C# inside the running Unity Editor via clibridge4unity — one-off snippets, expression evaluation, object inspection, line-by-line traces. Use when you need to read or change something in Unity that no dedicated command (FIND/INSPECTOR/COMPONENT_SET/etc.) covers.
---

# Running C# inside Unity

Standard Unity/C# and shell knowledge applies; below is only what's specific to clibridge4unity.

`CODE_EXEC` / `CODE_EXEC_RETURN` ship their own Roslyn compiler — they work even when Unity's main thread is busy with another command, and don't require `COMPILE` first.

Prefer a dedicated command when one fits (structured, faster): `INSPECTOR path [--children] [--brief] [--filter X]`, `FIND name`, `COMPONENT_SET obj comp field value`, `COMPONENT_ADD/COMPONENT_REMOVE`, `ASSET_SEARCH`/`ASSET_DISCOVER`, `MENU path`. Use `CODE_EXEC*` only when no command fits.

## Variants

```bash
clibridge4unity CODE_EXEC 'Debug.Log("hi");'     # fire-and-forget; alias: EXEC. Does NOT surface compile errors.
clibridge4unity CODE_EXEC_RETURN '1 + 2'         # waits, returns value + type; alias: EVAL
```

## Multi-line: write a file, pass the path

The CLI auto-detects an existing file-path arg and passes it directly (no temp copy) — dodges shell escaping and the 32KB Windows command-line limit. Write the file OUTSIDE the project: anything under `Assets/` or `Packages/` triggers a Unity import + domain reload and kills the pipe.

When the code contains `class ` or `namespace `, it's compiled as-is; otherwise the CLI wraps it in a generated `Runner` class with `using UnityEngine; using UnityEditor;` etc. already in scope.

## Flags

```bash
clibridge4unity CODE_EXEC_RETURN 'Selection.activeGameObject' --inspect 3 --private   # dump object tree (default depth 1)
clibridge4unity CODE_EXEC_RETURN @/tmp/script.cs --trace --vars pos,vel --skip Debug.Log   # --skip matches by literal substring, not glob
```

## Output

`CODE_EXEC_RETURN` prints the result followed by `(<type>)` using the short type name, e.g. `Player (GameObject)`.

## COMPILE vs CODE_EXEC

`CODE_EXEC*` runs in an isolated assembly — it cannot reference internal types you just added to user code that Unity hasn't compiled yet. To call into freshly-added code: edit the file → let Unity recompile (focus the editor or run `COMPILE`) → then `CODE_EXEC_RETURN` can reference it.
