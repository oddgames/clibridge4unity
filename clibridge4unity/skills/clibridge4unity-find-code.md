---
name: clibridge4unity-find-code
description: START HERE when beginning any Unity task (feature, bugfix, refactor) and you don't yet know which files, scenes, or prefabs are involved — run MAP before grepping or opening files. Also for symbol-aware code search — definitions, usages, derived types, GetComponent sites, members — and serialized wiring (which scenes/prefabs use a script or asset, what references a prefab). Use MAP for task orientation; ANALYZE when grep would be too noisy or you need symbol/wiring results (e.g. "where is class X used", "what overrides Update", "which prefabs is this attached to").
---

# Finding where to work in a Unity project

`MAP` and `ANALYZE` are **CLI-side**: they run offline against a persistent daemon index (Unity need not be open). The daemon indexes both the C# source (Roslyn) and the **serialized asset graph** — the GUID wiring in `.unity`/`.prefab`/`.asset` files that code-only tools can't see. Use them for symbol/wiring queries; use grep for literal text.

## Start from the task, not a symbol

When you know what you want to change but not where it lives, ask for a map first — before grep, before opening files:

```bash
clibridge4unity MAP double jump
clibridge4unity MAP shop purchase ui
```

Returns one dossier: matching scripts (kind-tagged `: MonoBehaviour` / `: ScriptableObject` / `[editor]`, with the scenes/prefabs they're **attached** to), scenes (with build-settings index), prefabs, ScriptableObject config assets, UXML/input-action assets, and UnityEvent wiring (`HUD.prefab → PlayerController.Jump`). It ends with a suggested `Next:` drill-down.

## Deep view of a type

```bash
clibridge4unity ANALYZE PlayerController
```

Returns: definition file/line, all usages, derived types, GetComponent sites, the class's own members — plus an **Asset wiring** section: which scenes/prefabs have it attached, ScriptableObject instances, and UnityEvent targets from serialized data.

## Reverse asset lookup — "what uses this?"

```bash
clibridge4unity ANALYZE usedby:Assets/Prefabs/Player.prefab
clibridge4unity ANALYZE usedby:PlayerController
```

Instant (index-backed) reverse GUID lookup: every scene/prefab/SO/UXML that references the asset or script. Catches inspector-assigned references, nested prefab links, and UnityEvent wiring that grep can't resolve. (Not covered: `Resources.Load`/Addressables-by-key — those are string refs in code.)

## Zoom into one member

```bash
clibridge4unity ANALYZE PlayerController.Move
clibridge4unity ANALYZE PlayerController.health
```

## Kind-prefixed cross-codebase search

```bash
clibridge4unity ANALYZE method:OnTriggerEnter   # every OnTriggerEnter, with signatures
clibridge4unity ANALYZE field:health
clibridge4unity ANALYZE property:IsAlive
clibridge4unity ANALYZE inherits:MonoBehaviour  # derived types
clibridge4unity ANALYZE attribute:SerializeField
```

## Notes

- The workflow is `MAP <task>` → `ANALYZE <TopHit>` → read the files it points at.
- First invocation may print `[roslyn] indexing N/M` on stderr while the daemon catches up; subsequent calls are sub-second.
- The daemon watches the filesystem — newly-created `.cs` files and edited scenes/prefabs are picked up without restarting Unity.
- Results include file path + line for each hit.
- `CODE_ANALYZE` and `CODE_SEARCH` are accepted aliases for `ANALYZE` (its former names).
