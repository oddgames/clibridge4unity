---
name: clibridge4unity-lint
description: Diagnose a suspected Unity compile error. Use LINT/COMPILE reactively — only when STATUS shows errors or behavior is unexpected, NEVER as a routine "did my edit compile?" check after every change.
---

Standard compile/lint discipline applies; below is only what's specific to this CLI. Default after editing C#: do nothing (Unity auto-recompiles on focus). Escalate only on evidence of a problem.

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

`COMPILE` — last resort. Triggers Unity's real compilation + domain reload: breaks open pipes (clients reconnect), re-runs source generators and post-compile callbacks, reloads all Editor assemblies. Use only when you need generators/post-compile callbacks, `LINT unity` was inconclusive and you need ground truth, or the user asked for a full recompile. CLI returns immediately — reconnect and run `STATUS` for the result.

## Don'ts
- Don't `COMPILE` after every edit (Unity does it on focus; it breaks in-flight bridge work).
- Don't `LINT` proactively; don't loop `STATUS` until clean.

## Reading errors
```bash
clibridge4unity LOG errors
clibridge4unity LOG errors verbose last:5
clibridge4unity LOG clear            # clears buffer after fixing
```
