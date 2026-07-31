---
name: clibridge4unity-lint
description: Diagnose a suspected Unity compile error. Use LINT/COMPILE reactively — only when STATUS shows errors or behavior is unexpected, NEVER as a routine "did my edit compile?" check after every change.
---

Standard compile/lint discipline applies; below is only what's specific to this CLI. Default after editing C#: do nothing — Unity auto-recompiles on focus, and 99% of the time the user has already compiled by the time they ask you to test. Assume compiled; escalate only on evidence of a problem (wrong results, errors in STATUS, CODE_EXEC can't see a new type).

## Tools, cheapest first

`STATUS` — instant. Is Unity compiling, are there errors, what mode. No errors → stop.

`LINT` — sub-second offline syntax check via Roslyn daemon (missing braces, unclosed strings, bad keywords, malformed decls). Daemon `FileSystemWatcher` picks up newly-created files Unity hasn't seen yet.
```bash
clibridge4unity LINT                 # syntax-only
clibridge4unity LINT warnings        # include warnings
```

`LINT unity` — ~5–30s per-asmdef compile. Parses every `.asmdef`, builds the dependency DAG, compiles each user asmdef with correct refs + defines + `UNITY_EDITOR` scoping (asmdef-aware, no cross-asmdef false positives). Catches missing methods, wrong arg counts, type errors, missing usings. Internal budget ~30s (10s no-progress watchdog); daemon read-timeout 60s. On budget exceed: returns partial results and recommends `COMPILE` — does not auto-run it.
```bash
clibridge4unity LINT unity
clibridge4unity LINT unity warnings
```

`COMPILE` — last resort. Triggers Unity's real compilation + domain reload: breaks open pipes (clients reconnect), re-runs source generators and post-compile callbacks, reloads all Editor assemblies. Use only when you need generators/post-compile callbacks, `LINT unity` was inconclusive and you need ground truth, or the user asked for a full recompile. The CLI waits through the reload and streams progress — expect minutes on a large project.

## COMPILE: never pipe it, never loop it

**Never `COMPILE ... | tail -N` / `| head -N`.** Both buffer until EOF, so a multi-minute reload prints nothing and looks hung — that gets it killed mid-compile. The output *is* the progress. Run it bare; filter afterwards with `LAST`.

**Never wrap it in a retry loop.** It is guarded CLI-side; read the verdict:
- `skipped: uptodate` (exit 0) — sources all older than the compiled assemblies. Already compiled. This is success.
- `skipped: looping` (exit 1) — repeated attempts, nothing changed between them. Something is blocking compilation; retrying cannot clear it. Run `STATUS`/`DIAG` and fix the cause.
- `COMPILE force` bypasses both.

Blocked states are terminal, not transient — waiting does not help:
- *"Cannot compile during play mode"* → `STOP` first.
- *"Unity is in the middle of a Player Build"* → wait for the build; every command is blocked until it ends.

Branch on the exit code, not on grepping stdout (`0` ok · `11` compile errors · `12` play mode · `13` safe mode · `14` timeout · `10` no connection). A clean compile emits no error lines, so a grep for `error` can't tell success from a filter miss.

## Don'ts
- Don't `COMPILE` after every edit (Unity does it on focus; it breaks in-flight bridge work).
- Don't run `LINT unity` *and* `COMPILE` — `LINT unity` is the cheap substitute for `COMPILE`, so doing both pays twice and answers nothing new.
- Don't `LINT` proactively; don't loop `STATUS` until clean.
- Don't treat STATUS's `scriptsModified`/`compileRecommended` alone as a trigger — that's informational. Act only when output is actually wrong or errors are shown.

## Reading errors
```bash
clibridge4unity LOG errors
clibridge4unity LOG errors verbose last:5
clibridge4unity LOG clear            # clears buffer after fixing
```
