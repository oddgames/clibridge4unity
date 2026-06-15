---
name: clibridge4unity-find-code
description: Search and inspect C# code in a Unity project — find definitions, usages, derived types, GetComponent sites, members. Use CODE_ANALYZE when grep would be too noisy or you need symbol-aware results (e.g. "where is class X used", "what overrides Update", "show me everything that has [SerializeField]").
---

# Finding code in a Unity project

`CODE_ANALYZE` is **CLI-side** — it runs against a persistent Roslyn daemon's index of the project's C#. It works offline (Unity doesn't need to be open) and returns symbol-aware results instead of raw text matches.

Prefer `CODE_ANALYZE` over `grep` when:
- You want every place a class is used (not just where its name appears in a string)
- You want overrides / implementations / derived types
- You want to zoom into one method or field across the codebase
- You need to know which class a method belongs to

Use `grep` / Grep tool when you want literal text (comments, strings, log messages).

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

- First invocation may show `(indexing: N/M)` while the daemon catches up; subsequent calls are sub-second.
- The daemon watches the filesystem — newly-created `.cs` files are picked up without restarting Unity.
- Results include file path + line for each hit; use the Read tool to follow up.
