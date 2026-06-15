---
name: clibridge4unity-run-code
description: Execute C# inside the running Unity Editor via clibridge4unity — one-off snippets, expression evaluation, object inspection, line-by-line traces. Use when you need to read or change something in Unity that no dedicated command (FIND/INSPECTOR/COMPONENT_SET/etc.) covers.
---

# Running C# inside Unity

`CODE_EXEC` / `CODE_EXEC_RETURN` ship their own Roslyn compiler — they work even when Unity's main thread is busy with another command, and they don't require `COMPILE` first.

## Decide first: is there a dedicated command?

Before reaching for `CODE_EXEC_RETURN`, check whether one of these already does what you want — they're structured, faster, and easier to read:

- `INSPECTOR path [--children] [--brief] [--filter X]` — dump GameObject + components + serialized fields
- `FIND name` — find a GameObject by name (scene or prefab)
- `COMPONENT_SET obj comp field value` — change a serialized field
- `COMPONENT_ADD / COMPONENT_REMOVE` — add/remove components
- `ASSET_SEARCH` / `ASSET_DISCOVER` — find assets
- `MENU path` — execute a menu item

Use `CODE_EXEC*` only when no command fits.

## Two variants

```bash
# Fire-and-forget (no return value, no waiting)
clibridge4unity CODE_EXEC 'Debug.Log("hello");'
clibridge4unity EXEC      'Debug.Log("hello");'   # alias

# Wait for result, returns value + type
clibridge4unity CODE_EXEC_RETURN '1 + 2'
clibridge4unity EVAL              '1 + 2'         # alias
clibridge4unity CODE_EXEC_RETURN 'GameObject.Find("Player").transform.position'
```

## Multi-line scripts: write a file, pass the path

The CLI auto-detects file paths — if the argument is an existing file, it's passed directly with no temp-file copy. This dodges shell escaping of `$`, `"`, backticks, and the 32KB Windows command-line limit.

```bash
# Write to a temp file OUTSIDE the Unity project — anything under Assets/ or
# Packages/ triggers a Unity asset import + recompile, killing the pipe.
TMP="$(mktemp --suffix=.cs)"   # macOS/Linux
# Windows: $TMP = Join-Path $env:TEMP "snippet_$(Get-Date -Format yyyyMMddHHmmss).cs"

cat > "$TMP" <<'CSHARP'
var go = GameObject.Find("Player");
return go != null ? go.transform.position : Vector3.zero;
CSHARP

clibridge4unity CODE_EXEC_RETURN "$TMP"
```

When the code contains `class ` or `namespace `, it's compiled as-is; otherwise the CLI wraps it in a generated `Runner` class with `using UnityEngine; using UnityEditor;` etc. already in scope.

## Flags

```bash
# Dump the result object tree (default depth 1)
clibridge4unity CODE_EXEC_RETURN 'Selection.activeGameObject' --inspect 3 --private

# Line-by-line trace
clibridge4unity CODE_EXEC_RETURN @/tmp/script.cs --trace

# Trace, but only show specific variables
clibridge4unity CODE_EXEC_RETURN @/tmp/script.cs --trace --vars pos,vel --skip 'Debug.Log*'
```

## Output

- `CODE_EXEC_RETURN` prints the result followed by `(<type>)`, e.g. `Player (UnityEngine.GameObject)`.
- Compilation errors include source-line context.
- Runtime exceptions include a stack trace.

## When to prefer COMPILE over CODE_EXEC

`CODE_EXEC*` runs in an isolated assembly — it cannot reference internal types you just added to user code that Unity hasn't compiled yet. If you need to call into freshly-added code in the user's scripts, edit the file → let Unity recompile (focus the editor or run `COMPILE` if needed) → then `CODE_EXEC_RETURN` can reference it.

## Don't

- Don't write your snippet under `Assets/` or `Packages/` — that triggers an import + domain reload and breaks the pipe.
- Don't use `CODE_EXEC` when `CODE_EXEC_RETURN` would tell you whether the snippet succeeded. The fire-and-forget variant doesn't surface compile errors.
