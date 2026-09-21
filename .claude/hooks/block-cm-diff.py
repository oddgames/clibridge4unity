#!/usr/bin/env python
"""PreToolUse hook: refuse GUI-launching Plastic SCM diff commands.

`cm diff <file>` / `cm diff rev:...` / `cm difftool` / `cm gui` open a desktop
window that an agent can't read, so the diff is lost and the window sits there
until a human closes it. Redirect to the text diff script instead (same thing
/diff runs). Changeset/branch/shelve-level `cm diff cs:N` lists items as text
and is allowed.
"""
import json, re, sys

try:
    data = json.load(sys.stdin)
except Exception:
    sys.exit(0)

if data.get("tool_name") != "Bash":
    sys.exit(0)

cmd = data.get("tool_input", {}).get("command", "")

# `cm diff`, `cm difftool`, `cm gui` (+ cm.exe / full path), anywhere in a pipeline
m = re.search(r"(^|[\s;&|(])(?:\S*[\/])?cm(?:\.exe)?\s+(diff|difftool|gui)\b\s*(\S*)", cmd, re.I)
if not m:
    sys.exit(0)

# cs:/br:/sh: specs print a text listing — no GUI. rev: and bare paths open the visual diff.
if m.group(2).lower() == "diff" and re.match(r"(cs|br|sh):", m.group(3), re.I):
    sys.exit(0)

print(
    "BLOCKED: `cm diff <file|rev:>` / `cm difftool` / `cm gui` open a Plastic SCM GUI "
    "window that cannot be read from here. Use the text diff instead:\n"
    "  python .claude/scripts/pdiff.py              # all changed files\n"
    "  python .claude/scripts/pdiff.py <path>       # one file\n"
    "  python .claude/scripts/pdiff.py --stat       # summary only\n"
    "(or the /diff skill). `cm status`, `cm cat` and changeset-level\n"
    "`cm diff cs:N` / `br:/x` / `sh:N` are text-only and stay allowed.",
    file=sys.stderr,
)
sys.exit(2)
