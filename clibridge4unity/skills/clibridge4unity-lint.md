---
name: clibridge4unity-lint
description: Diagnose a suspected Unity compile error. Use LINT/COMPILE reactively — only when STATUS shows errors or behavior is unexpected, NEVER as a routine "did my edit compile?" check after every change.
---

# Compile / lint discipline

**Default behavior after editing C#: do nothing.** Unity auto-recompiles when its window gains focus. `STATUS` is the cheap signal — it tells you whether compile is dirty and whether errors exist.

```bash
clibridge4unity STATUS
```

Only escalate to `LINT` / `COMPILE` when there's evidence of a problem.

## The three tools, cheapest first

### 1. `STATUS` — always your first check (instant)

Tells you: is Unity compiling right now, are there errors, what mode is it in. If `STATUS` reports no errors, **stop**. Don't lint, don't compile.

### 2. `LINT` — sub-second, offline (use when STATUS shows errors or you need a faster signal than waiting for Unity)

Offline syntax-only check via the Roslyn daemon. Catches missing braces, unclosed strings, bad keywords, malformed declarations. The daemon's `FileSystemWatcher` picks up newly-created files Unity hasn't seen yet — useful when Unity hasn't even attempted to compile a new file.

```bash
clibridge4unity LINT                 # syntax-only, sub-second
clibridge4unity LINT warnings        # include warnings
```

### 3. `LINT unity` — per-asmdef compile, ~5–60s (use when LINT is too shallow)

Parses every `.asmdef`, builds the dependency DAG, compiles each user asmdef with correct refs + defines + `UNITY_EDITOR` scoping. Catches missing methods, wrong arg counts, type errors, missing usings. Asmdef-aware so no cross-asmdef false positives. Caps at 60s — falls back to `COMPILE` if exceeded.

```bash
clibridge4unity LINT unity
clibridge4unity LINT unity warnings
```

### 4. `COMPILE` — last resort, expensive

Triggers Unity's actual compilation, which causes a domain reload. **Side effects:**
- Breaks any open pipe connections (clients reconnect after Unity finishes).
- Re-runs source generators and post-compile callbacks.
- Reloads all Editor assemblies — slow.

Only run `COMPILE` when:
- You actually need source generators / post-compile callbacks to fire.
- `LINT unity` was inconclusive and you need Unity's ground truth.
- The user explicitly asked for a full recompile.

After `COMPILE`, the CLI returns immediately; reconnect and run `STATUS` to see the result.

## Don'ts

- **Don't `COMPILE` after every C# edit.** Unity does it itself on focus. This is the #1 way to waste the user's time and break in-flight bridge work.
- **Don't `LINT` proactively.** It's cheap but adds noise on green-path edits. Run it when there's a reason.
- **Don't loop `STATUS` until clean** — the user can also see the editor; trust them or wait for an event-driven signal.

## Reading error output

`LOG errors` shows the current console errors. `LOG errors verbose` adds full stack traces.

```bash
clibridge4unity LOG errors
clibridge4unity LOG errors verbose last:5
clibridge4unity LOG clear            # clears the buffer after fixing
```
