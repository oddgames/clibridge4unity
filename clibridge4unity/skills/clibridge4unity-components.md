---
name: clibridge4unity-components
description: Add, remove, and configure components on a GameObject. Use when the task is "set this field on that component" or "add a component to this object" — not for creating GameObjects or assets (see clibridge4unity-scene / clibridge4unity-prefab / clibridge4unity-assets).
---

# Components

Standard Unity component/serialization rules apply (serialized fields often `m_FieldName`, type names case-sensitive). Below is only the clibridge4unity surface.

```bash
clibridge4unity COMPONENT_ADD    Canvas/Panel BoxCollider
clibridge4unity COMPONENT_REMOVE Canvas/Panel BoxCollider

# COMPONENT_SET plain args: gameObject component field value
clibridge4unity COMPONENT_SET Canvas/Panel Image m_Color "#FF0000"
clibridge4unity COMPONENT_SET Player Rigidbody mass 5
clibridge4unity COMPONENT_SET Player Transform localPosition "1,2,3"

# Inspect to discover serialized field names/types/current values
clibridge4unity INSPECTOR Canvas/Panel
```

Compound values: vectors/colors accept `"x,y,z"` and hex / named colors. JSON form for anything more involved:

```bash
clibridge4unity COMPONENT_SET '{"gameObject":"Canvas/Panel","component":"RectTransform","field":"sizeDelta","value":{"x":200,"y":50}}'
```

On "no such field/property": confirm component type (case matters) and re-run `INSPECTOR` for the actual name.

## Related (route elsewhere)
- `clibridge4unity-run-code` — components needing construction logic, persistent `UnityEvent` listeners (`UnityEditor.Events.UnityEventTools.AddPersistentListener`), or bulk loops over many objects.
- `clibridge4unity-scene` / `clibridge4unity-prefab` / `clibridge4unity-assets` — creating GameObjects or assets.
