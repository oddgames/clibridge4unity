Show text diffs from Plastic SCM (no GUI). Runs `python .claude/scripts/pdiff.py`.

```bash
python .claude/scripts/pdiff.py $ARGUMENTS
```

If the user provides a file path, pass it as an argument. Otherwise it diffs all changed files.
Use `--stat` for a summary view.

After showing the diff output, briefly summarize what changed.
