---
name: clibridge4unity-tests
description: Run Unity's EditMode and PlayMode tests through clibridge4unity. Use when the task is "run the tests", "did test X pass", or verifying a code change with the existing test suite.
---

# Tests

`TEST` streams test results as they run. Output includes per-test pass/fail and final summary.

## Modes

```bash
clibridge4unity TEST              # default: EditMode
clibridge4unity TEST editmode
clibridge4unity TEST playmode
clibridge4unity TEST all          # EditMode + PlayMode
```

## Filters (all OR'd — a test runs if it matches any)

```bash
# Positional groups — match class/namespace paths (regex)
clibridge4unity TEST PlayerTests
clibridge4unity TEST PlayerTests,EnemyTests           # comma OR
clibridge4unity TEST PlayerTests EnemyTests           # space-separated also OR'd

# By [Category("X")] attribute
clibridge4unity TEST --category Smoke,Integration

# By exact test full name
clibridge4unity TEST --tests MyNs.PlayerTests.CanJump,MyNs.PlayerTests.CanRun

# Combine — runs if it matches ANY group OR category OR exact name
clibridge4unity TEST PlayerTests --category Smoke
```

## List available tests

```bash
clibridge4unity TEST list
clibridge4unity TEST list Player    # substring filter
```

## Common gotchas

- **PlayMode tests** require entering play mode. Don't run them while you're already paused/stepping through play mode for something else.
- **A failing test isn't always a code regression** — Unity may not have recompiled. Check `STATUS` first; if compile is dirty, let it settle.
- Use `LAST -tail 80` to re-read the end of the last test run (summary + failures) without re-running the suite.

## Run a test then inspect failures

```bash
clibridge4unity TEST PlayerTests
clibridge4unity LAST -grep FAIL    # filter the cached output for failure lines
clibridge4unity LAST -tail 40      # tail of the run for context
```
