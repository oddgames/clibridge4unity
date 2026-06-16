---
name: clibridge4unity-tests
description: Run Unity's EditMode and PlayMode tests through clibridge4unity. Use when the task is "run the tests", "did test X pass", or verifying a code change with the existing test suite.
---

# Tests

Standard Unity test-runner knowledge applies; below is the clibridge4unity-specific surface. `TEST` streams per-test pass/fail plus a final summary.

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

## Gotchas

- Don't run PlayMode tests while already paused/stepping through play mode for something else.
- A failing test may mean Unity hasn't recompiled — check `STATUS` first; if compile is dirty, let it settle.
- `LAST -tail 80` re-reads the end of the last run (summary + failures) without re-running.

## Inspect failures after a run

```bash
clibridge4unity LAST -grep FAIL    # filter cached output for failure lines
clibridge4unity LAST -tail 40      # tail of the run for context
```
