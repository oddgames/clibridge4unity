---
name: clibridge4unity-find-code
description: Search and inspect C# code in a Unity project — find definitions, usages, derived types, GetComponent sites, members. Use CODE_ANALYZE when grep would be too noisy or you need symbol-aware results (e.g. "where is class X used", "what overrides Update", "show me everything that has [SerializeField]").
---

# Finding code in a Unity project

`CODE_ANALYZE` is **CLI-side**: runs offline against a persistent Roslyn daemon index (Unity need not be open), returning symbol-aware results. Use it for symbol queries (usages, overrides, members); use grep for literal text. Standard grep-vs-symbol tradeoffs are general knowledge.

## Deep view of a type

```bash
clibridge4unity CODE_ANALYZE PlayerController
```

Returns: definition file/line, all usages, derived types, GetComponent sites, the class's own members.

## Zoom into one member

```bash
clibridge4unity CODE_ANALYZE PlayerController.Move
clibridge4unity CODE_ANALYZE PlayerController.health
```

## Kind-prefixed cross-codebase search

```bash
clibridge4unity CODE_ANALYZE method:OnTriggerEnter   # every OnTriggerEnter, with signatures
clibridge4unity CODE_ANALYZE field:health
clibridge4unity CODE_ANALYZE property:IsAlive
clibridge4unity CODE_ANALYZE inherits:MonoBehaviour  # derived types
clibridge4unity CODE_ANALYZE attribute:SerializeField
```

## Notes

- First invocation may print `[roslyn] indexing N/M` on stderr while the daemon catches up; subsequent calls are sub-second.
- The daemon watches the filesystem — newly-created `.cs` files are picked up without restarting Unity.
- Results include file path + line for each hit.
