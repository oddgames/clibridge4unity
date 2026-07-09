---
name: clibridge4unity-release-notes
description: When a bug smells like Unity itself rather than your code — a hard-to-reproduce engine/editor glitch, something that "worked before the upgrade," a regression after changing Unity versions, or long-standing weirdness that might already be fixed — check Unity's official release notes for a matching known issue/fix. `RELEASENOTES <from> <to>` pulls them (pure HTTP, no Unity/project needed). Auto-trigger on "is this a known Unity bug", "regression after updating Unity", "worked before the upgrade", "known issue", "was this fixed in a later version", "UUM-", engine/editor bug you can't explain from your own code, or deciding whether to upgrade a patch to get a fix.
---

# Check Unity release notes for a known issue/fix

When a bug looks like Unity's fault (not yours) — an engine/editor glitch, a post-upgrade regression, or a mystery that survives your own code review — Unity may already know about it. `RELEASENOTES` pulls the official "Compare Versions" notes for a version range so you can match your symptom to a documented fix or regression. Offline of Unity (pure HTTP, no pipe/project).

## Workflow for a hard-to-find bug

1. **Run it bare** — from a project dir, `clibridge4unity RELEASENOTES` reads your Unity version from `ProjectSettings/ProjectVersion.txt` and pulls everything from your version up to the latest release ("what's changed since mine"). It echoes the resolved `from`/`to` on stderr.
2. **Search for your symptom** — grep the note text, not just categories:
   ```bash
   clibridge4unity RELEASENOTES -g "Terrain.*NullReference|SpriteRenderer mask"
   ```
3. **Narrow by subsystem** if noisy — `-c Graphics,URP,Shaders,IL2CPP,Physics,Editor,Scripting,Android,…` (substring, comma-OR).
4. **Read the hits both directions:**
   - A **`Fixed …`** note in a patch *later* than yours that matches → upgrading to that patch is the fix.
   - A change/regression at the version you *upgraded into* → that's the likely cause of a new bug. The `UUM-####` link goes to the issue tracker (repro steps + current status).

## Command

```bash
clibridge4unity RELEASENOTES                          # bare: current project version -> latest available
clibridge4unity RELEASENOTES 6000.5.2f1               # from given, to defaults to latest
clibridge4unity RELEASENOTES <from> <to>              # explicit range, grouped by category → version
clibridge4unity RELEASENOTES -g "regex"               # filter note text (case-insensitive) — best for a specific symptom
clibridge4unity RELEASENOTES <from> <to> -c URP,Shaders
clibridge4unity RELEASENOTES --format json            # for piping into other tooling
clibridge4unity RELEASENOTES --list 6000.4            # browse available versions
```
Aliases: `UNITYNOTES`, `RELNOTES`. `--url` prints the source page URL; `-o <file>` writes to disk. All flags combine with the bare/defaulted range.

## Pair it

- `EDITORLOG errors` (or `LOG errors`) tells you *what* crashed/threw; `RELEASENOTES -g "<that error>"` tells you whether Unity already fixed it. Run them together on a mystery bug. See `clibridge4unity-bridge`.
- Device-only rendering bugs → `clibridge4unity-shaders` first (editor-vs-device divergence is usually your code, not Unity's), then release notes if it still smells like an engine regression.
- Build/preprocess failures → `clibridge4unity-build`; if a build step regressed after an upgrade, check the notes for that version.
