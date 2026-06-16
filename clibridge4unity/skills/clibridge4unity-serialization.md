---
name: clibridge4unity-serialization
description: Use for ANY field-on-MonoBehaviour-or-ScriptableObject question — what does/doesn't serialize, how to handle polymorphism, list/dict storage, migration when a field is renamed, `ISerializationCallbackReceiver`, or "the inspector value disappeared after I changed the type." Auto-trigger on `[SerializeField]`, `[SerializeReference]`, `[FormerlySerializedAs]`, `ISerializationCallbackReceiver`, `OnBeforeSerialize`, `OnAfterDeserialize`, `ScriptableObject`, `[Serializable]`, `Dictionary<TKey,TValue>` on a MonoBehaviour field, polymorphic inspector lists, interface-typed fields, "field is null after reload but I assigned it", "renamed field lost its value", "abstract base in list", "Sirenix/Odin serialization", asset migrations. Get the rules wrong and assigned references silently vanish on the next save.
---

# Unity Serialization

The standard Unity serialization rules apply here — what does/doesn't serialize, `[SerializeField] private`, `[SerializeReference]` for polymorphism/null/cycles, `ISerializationCallbackReceiver` (off the main thread, no Unity API inside) for `Dictionary` via parallel key/value lists, `[FormerlySerializedAs]` for field renames, `[MovedFrom]` for `[SerializeReference]` type renames, enums-as-integers, and the 7/10 nesting-depth limit. Apply them as standard knowledge.

House convention: prefer `[SerializeReference]` + `[Serializable]` over Odin/Sirenix when the YAML must stay text-mergeable — Odin stores a `byte[]` blob that breaks Plastic/Git diffs. Reach for Odin only when the domain genuinely needs what Unity can't do (multi-key dictionaries, deep generics, polymorphic graphs). Decide per-asset, not per-project.

## Verify from the bridge

`INSPECTOR <asset-or-scene-object>` dumps the serialized fields actually written to disk — the fast "did it persist?" check after a type/field change or a `[FormerlySerializedAs]` migration (`REIMPORT` the asset first). A field that's missing from the output, or back at its default after a save/reimport, means the type isn't serializing (a `Dictionary`, an un-`[SerializeReference]`'d interface/abstract base, or a rename with no `[FormerlySerializedAs]`).

## Related
- `clibridge4unity-prefab-workflow` — serialized fields across instance overrides/variants
- `clibridge4unity-domain-reload` — on-disk serialization vs static memory
- `clibridge4unity-editor-tools` — `SerializedProperty` for custom inspectors (undo/multi-edit)
