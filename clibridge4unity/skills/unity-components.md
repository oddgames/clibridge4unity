---
name: unity-components
description: Add, remove, and configure components on a GameObject. Use when the task is "set this field on that component" or "add a component to this object" — not for creating GameObjects or assets (see unity-scene / unity-prefab / unity-assets).
---

# Components

Plain-args form works for the common cases; JSON form is available for anything more complex.

## Add / remove

```bash
clibridge4unity COMPONENT_ADD    Canvas/Panel BoxCollider
clibridge4unity COMPONENT_REMOVE Canvas/Panel BoxCollider
```

## Set a field or property

```bash
# Plain args: gameObject component field value
clibridge4unity COMPONENT_SET Canvas/Panel Image m_Color "#FF0000"
clibridge4unity COMPONENT_SET Player Rigidbody mass 5
clibridge4unity COMPONENT_SET Player Transform localPosition "1,2,3"
```

Field/property names use Unity's serialized-field convention (often `m_FieldName`). When unsure, inspect first:

```bash
clibridge4unity INSPECTOR Canvas/Panel
```

That dumps all serialized fields with their current values and types — pick the right name from the output.

## Compound values

Vectors and colors accept `"x,y,z"` and hex / named colors. For anything more involved use the JSON form:

```bash
clibridge4unity COMPONENT_SET '{"gameObject":"Canvas/Panel","component":"RectTransform","field":"sizeDelta","value":{"x":200,"y":50}}'
```

## When the field doesn't exist

If `COMPONENT_SET` errors with "no such field/property", verify the component type is correct (case matters) and re-run `INSPECTOR` to see the actual field name. Some derived types have differently-named fields than their base.

## When this isn't the right tool

- Adding a component that requires construction logic (e.g. setting up serialized references): write a snippet via `unity-run-code`.
- Wiring up `UnityEvent`s persistently: `COMPONENT_SET` can serialize simple values but not full persistent listeners — use `unity-run-code` with `UnityEditor.Events.UnityEventTools.AddPersistentListener`.
- Bulk changes across many objects: `unity-run-code` with a loop is usually clearer than many `COMPONENT_SET` calls.
