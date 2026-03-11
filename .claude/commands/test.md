Run the clibridge4unity integration test suite against the running Unity project.

```bash
python -m pytest tests/ -v --tb=short $ARGUMENTS
```

Arguments: optional pytest filter (e.g. `-k core`, `-k "Ping"`, `tests/test_scene.py`, `--last-failed`).

After the tests complete, analyze the output:
- If all tests passed, say so briefly
- If tests failed, diagnose what went wrong and suggest fixes
- If Unity isn't connected, tell the user to open Unity with UnityTestProject
