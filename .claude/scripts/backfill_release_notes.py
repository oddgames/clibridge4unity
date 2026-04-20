#!/usr/bin/env python
"""Retroactively fill GitHub release notes for this session's versions.

Each entry is a markdown body applied via `gh release edit <tag> --notes-file ...`.
Older tags (pre-v1.0.57) keep GitHub's auto-generated notes — this script only
updates the ones we have accurate session context for.
"""
import os
import subprocess
import sys
import tempfile


NOTES = {
    "v1.0.57": """First big pass this session — mostly UX + unification work.

### CODE_ANALYZE / CODE_SEARCH
- `CODE_ANALYZE` now accepts prefix queries: `method:`, `field:`, `property:`, `inherits:`, `attribute:`.
- `CODE_SEARCH` kept as a deprecated alias that forwards through.
- Raw-grep default mode dropped (use Grep directly).

### INSPECTOR
- New `--depth N` and `--children` flags for subtree inspection (scene GameObjects) — closes the gap that made agents reach for `CODE_EXEC_RETURN` just to walk a hierarchy.

### CODE_EXEC
- CLI help + generated `CLAUDE.md` now explicitly recommend writing multi-line scripts to a temp file and passing `@path`, and putting the file **outside** `Assets/` / `Packages/` to avoid triggering a recompile.

### Generated CLAUDE.md
- Fixed the marker-mismatch bug that caused SETUP re-runs to append duplicate blocks instead of replacing.
- Content refreshed to reflect current commands.

### SetupWizard
- Now downloads the CLI from GitHub Releases instead of the removed bundled `Tools/` folder.
""",

    "v1.0.58": """Hotfix for v1.0.57.

Fixed `CS1737: Optional parameters must appear after all required parameters` in `PrefabCommands.BuildHierarchy` — a `ref` parameter was trailing a default-value parameter, which broke Unity compile of the Package.
""",

    "v1.0.59": """`CODE_EXEC` help polish.

The inline Usage string on `CODE_EXEC` / `CODE_EXEC_RETURN` (and the generated `CLAUDE.md`) now explicitly warns: **put the temp `.cs` file OUTSIDE the Unity project** (`$TEMP`, `/tmp`, `~/.cache`) — writing it under `Assets/` or `Packages/` triggers a Unity asset import + recompile, which kills the pipe.
""",

    "v1.0.60": """`CODE_EXEC` path-shape guard.

If the data for `CODE_EXEC` / `CODE_EXEC_RETURN` looks like a file path (drive letter, `/`, `./`, `~/`, UNC) **and** ends in `.cs` but doesn't exist, the CLI now refuses to treat it as inline C#. Previously the path string was silently compiled as source and failed with cryptic `CS1056: Unexpected character '\\'` errors.
""",

    "v1.0.61": """Multi-Unity workspace fix + `wmic` replacement for broken `System.Management`.

### Multi-Unity handling
- `unityPids` is now filtered to only PIDs that match the target project (via window title **and/or** `-projectPath`). Previously, when multiple Unity instances were open, dialogs from sibling instances (e.g. another project's IL2CPP build) leaked into the target's report.
- When no Unity matches the target, the error now lists every running Unity workspace (pid, project path, window title) so the user can see exactly what's open and pick one with `-d`.

### Under the hood
- `System.Management` was silently failing under trimmed single-file publish (`TypeInitializationException` on `ManagementPath`). The old WMI command-line reader had been broken the whole time; window-title matching was covering for it.
- Replaced with a shelled-out `wmic` reader. Dropped the `System.Management` package dependency entirely.
""",

    "v1.0.62": """Offline commands bypass Unity pre-flight + update banner moved to the bottom.

- `CODE_ANALYZE` dispatch moved *before* the Unity state pre-flight gates. Previously a busy/loading Unity would spuriously block offline Roslyn queries with "Unity is busy — Loading project". Now code queries run regardless of Unity state.
- `CODE_SEARCH` command removed entirely (shipped in this version — later restored as a legacy alias in v1.0.63 after user feedback).
- Update banner (available new version + release notes) now prints at the **bottom** of command output via a `Main` finally block, no longer throttled — the background fetch keeps the cache warm so there's zero delay.
""",

    "v1.0.63": """Legacy `CODE_SEARCH` alias + `UPDATE` regenerates per-project CLAUDE.md.

### CODE_SEARCH restored
Muscle memory (and old scripts) kept typing `CODE_SEARCH`, which in v1.0.62 fell into the generic Unity pre-flight and errored with "Unity busy" during loading. Re-added as a legacy alias that forwards to `CODE_ANALYZE` with a one-line `[deprecated]` notice, and correctly bypasses the Unity pre-flight.

### UPDATE refreshes CLAUDE.md
`clibridge4unity UPDATE` now auto-regenerates `CLAUDE.md` in the current project after a successful self-update (and on "already up to date" — template can shift between installs). Keeps AI tooling's command reference in sync with the CLI binary without needing a separate `SETUP` call.
""",

    "v1.0.64": """Shared `CodeAnalysisCore` + many more CODE_ANALYZE query shapes.

### Refactor
`RoslynDaemon` and `RoslynAnalyzer` (one-shot fallback) were separately maintaining ~650 lines of duplicate extraction logic. Both now delegate to a new `CodeAnalysisCore` with identical behaviour — future changes = one edit, not two.

### New query shapes handled correctly
- **Nested enums / types** — `CODE_ANALYZE Quality.ShadowQuality` now recognises a nested enum inside `Quality`, lists its values, and reports usages across the codebase (instead of returning methods that merely use the name as a parameter type).
- **Constructors** — `CODE_ANALYZE Foo.Foo` or `.ctor` hits.
- **Indexers** — `CODE_ANALYZE this` / `Item`.
- **Operators + conversion operators** — `operator+`, `op_Addition`, `operator MyType`.
- **Destructors** — `~Foo`.
- **Generic / array stripping** — `List<MyType>`, `MyGeneric<T>`, `Foo[][]` strip to the bare identifier before lookup.
- **Namespaces** — a query that's a namespace lists the types inside.
- **Using aliases** — `using Tex = Texture2D;` makes `CODE_ANALYZE Tex` resolve.
- **Partial classes** — `Defined in:` shows `(partial — split across N files)` when applicable.
- **Multi-dot paths** — `Generator.Trucks.PlayerTruck` now splits correctly (last-dot) and resolves against the nested-type's bare identifier.

### Bug fixes
- `PathResolver` precursor: member-fallback now uses exact identifier match instead of substring — a method named `OnEnable` no longer matches `Apply(OnEnable arg)`.
- Grep tail renamed to `Usages` under member-zoom for clarity; capped tighter when structured output already covers the usages.
""",

    "v1.0.65": """Session ledger + `--intent` + SESSIONS command + log cleanup.

### Multi-agent presence
- New `SessionLedger` drops a per-project session file per CLI invocation; every command prints a one-line banner for every other active agent at the top of its output.
- `--intent "..."` flag — descriptive annotation shown to other agents (e.g. `--intent "refactoring CameraController"`).
- `SESSIONS` command lists every active CLI agent on the project with pid, age, command, intent, and cwd.
- Presence-only: no locking, no RPC, no command rejection. Soft coordination for shared Unity instances.

### Log cleanup
- Timeout exceptions during domain reload / asset import now log as `Warning` instead of `Error` — stops spamming the Unity console red for expected events.
- Removed the noisy `[Bridge] ResolveCode: path=...` debug log that fired on every file-path `CODE_EXEC`.
- `CommandRegistry` now unwraps `TargetInvocationException` — user code that throws inside a main-thread action shows the real cause, not the reflection wrapper.
""",

    "v1.0.66": """`TEST` multi-filter.

`TEST` now accepts arrays for three filter axes, all OR'd by Unity's Test Framework:
- **Groups** (positional, comma- or space-separated) → `testFilter.groupNames`
- **`--category X,Y`** → `testFilter.categoryNames` (`[Category("…")]` attribute)
- **`--tests A,B`** → `testFilter.testNames` (exact full names)

Examples:
```
TEST PlayerTests,CameraTests
TEST --category Physics,AI
TEST --tests Foo.TestA,Foo.TestB
TEST MyTest --category Physics playmode
```

When any filter is set, the first output line echoes it: `Filter: groups=[...] categories=[...] tests=[...]  mode=EditMode`.

Caught mid-test: `CommandArgs.Parse` routes unknown tokens to `Warnings` (not `Positional`) when a flag schema is defined, so `TEST Foo,Bar` was silently matching nothing. Fixed by reading both lists for group tokens.
""",

    "v1.0.67": """Breaking: `SCENE`, `PREFAB_HIERARCHY`, `CODE_SEARCH` removed. Unified into `INSPECTOR` and `CODE_ANALYZE`.

### INSPECTOR expanded
- `INSPECTOR` with no args → whole active-scene hierarchy (brief, all roots recursed) — **replaces `SCENE`**.
- `INSPECTOR <prefab.path> --children [--brief] [--filter X]` — **replaces `PREFAB_HIERARCHY`**.
- `--filter X` matches GameObject name **OR** component name (substring). Closes the "find-by-name inside a prefab asset" gap.
- `--brief` skips serialized-field dumps — components only.
- `--max N` truncation with a warning (default 300 nodes).

### FIND scope prefixes
- `FIND Name` / `FIND scene:Name` — scene (default).
- `FIND prefab:Assets/UI/Menu.prefab/Button,Panel` — find by name **inside** a prefab asset. Comma-separate for OR.

### Deletions (no aliases)
- `SCENE` — replaced by `INSPECTOR` (no args).
- `PREFAB_HIERARCHY` — replaced by `INSPECTOR <path> --children --brief [--filter X]`.
- `CODE_SEARCH` — replaced by `CODE_ANALYZE` (prefix forms unchanged: `method:`, `field:`, etc.).

### Cleanup
- Deleted 4 orphan helpers from `PrefabCommands.cs` (BuildHierarchy, CountNodes, FindObjectsWithComponent, GetPrefabPath).
- README, root CLAUDE.md, and SETUP-generated CLAUDE.md all updated.
""",

    "v1.0.68": """`PathResolver` + "Did you mean?" suggestions on not-found errors.

### PathResolver
- New `PathResolver` helper: scene-GameObject scoring (exact / prefix / substring / token-overlap) + asset suggestions via `AssetDatabase.FindAssets`.
- New `Response.ErrorSceneNotFound(path)` / `Response.ErrorAssetNotFound(path, kind)` auto-append `Did you mean:` suggestions.
- Bulk-substituted 18 call sites across `Asset/`, `Component/`, `Prefab/`, and `Scene/` command files.

### Example
Before:
```
Error: GameObject not found: Maine Camera
```

After:
```
Error: GameObject not found: Maine Camera
Did you mean:
  Main Camera
```

Covers `GameObject not found`, `GameObject not found in scene`, `Asset not found`, `Prefab not found`, and `Instance not found` errors across `INSPECTOR`, `DELETE`, `COMPONENT_SET/ADD/REMOVE`, `PREFAB_INSTANTIATE`, `PREFAB_SAVE`, `ASSET_MOVE/COPY/DELETE`, and more.
""",
}


def main():
    for tag, body in NOTES.items():
        print(f"\n=== {tag} ===")
        tmp = os.path.join(tempfile.gettempdir(), f"release_notes_{tag}.md")
        with open(tmp, "w", encoding="utf-8") as f:
            f.write(body.strip())
        r = subprocess.run(
            f'gh release edit {tag} --notes-file "{tmp}"',
            shell=True, capture_output=True, text=True,
        )
        if r.returncode != 0:
            print(f"  FAILED: {r.stderr.strip()}", file=sys.stderr)
        else:
            print(f"  OK: https://github.com/oddgames/clibridge4unity/releases/tag/{tag}")
        try: os.remove(tmp)
        except Exception: pass


if __name__ == "__main__":
    main()
