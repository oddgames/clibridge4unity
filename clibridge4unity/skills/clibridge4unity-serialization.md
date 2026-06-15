---
name: clibridge4unity-serialization
description: Use for ANY field-on-MonoBehaviour-or-ScriptableObject question — what does/doesn't serialize, how to handle polymorphism, list/dict storage, migration when a field is renamed, `ISerializationCallbackReceiver`, or "the inspector value disappeared after I changed the type." Auto-trigger on `[SerializeField]`, `[SerializeReference]`, `[FormerlySerializedAs]`, `ISerializationCallbackReceiver`, `OnBeforeSerialize`, `OnAfterDeserialize`, `ScriptableObject`, `[Serializable]`, `Dictionary<TKey,TValue>` on a MonoBehaviour field, polymorphic inspector lists, interface-typed fields, "field is null after reload but I assigned it", "renamed field lost its value", "abstract base in list", "Sirenix/Odin serialization", asset migrations. Get the rules wrong and assigned references silently vanish on the next save.
---

# Unity Serialization

Unity has its own serializer with rules that differ from .NET binary or JSON. Its biggest sharp edges: only specific types serialize (no `Dictionary`, no generic interfaces, no polymorphism), and getting the rules wrong is silent — the inspector value goes back to default on save with no warning. Know what survives, and when to use `[SerializeReference]` / `ISerializationCallbackReceiver` / Sirenix-Odin / a custom wrapper.

## The one rule that prevents most of these

**Unity serializes fields whose declared type it recognises, by VALUE, by FIELD NAME.** If a type isn't recognised (a `Dictionary<>`, an interface, an abstract base on a list), it's not stored. If a field name changes, the stored value is orphaned. If a base type is concrete and the field is polymorphic, you get slicing.

## What Unity serializes (the recognised set)

- All primitive value types (`int`, `float`, `bool`, `string`, …).
- Unity's built-in struct types (`Vector2/3/4`, `Quaternion`, `Matrix4x4`, `Color`, `Rect`, `Bounds`, `LayerMask`, `AnimationCurve`, `Gradient`).
- Anything inheriting `UnityEngine.Object` (`GameObject`, `Component`, `Texture`, `Material`, `ScriptableObject`, …) — stored as a reference.
- Enums (stored as the underlying integer — see pitfall #6).
- `[Serializable]` plain classes / structs whose fields are all themselves serializable.
- `List<T>` or `T[]` where `T` is one of the above.
- That's it. **No `Dictionary`. No `HashSet`. No `Tuple`. No `IEnumerable`. No interface-typed fields without `[SerializeReference]`. No generic methods.**

## Pitfall catalog

### 1. `private` fields aren't serialized; `[SerializeField]` re-enables them
Default rule: `public` fields of a serializable type are serialized. `private` fields aren't, unless marked `[SerializeField]`. Conversely, `public` fields you DON'T want serialized need `[NonSerialized]`.
- **Rule:** use `[SerializeField] private` for inspector-editable internal state. Use `public` only when other code needs to read/write it; even then, consider exposing via a property and keeping the backing field `[SerializeField] private`. `[NonSerialized] public` for runtime-only public scratch.

### 2. `Dictionary<K,V>` is not serialized — use a SerializableDictionary wrapper
A `Dictionary<string, int>` on a MonoBehaviour is silently NOT saved. Inspector edits revert on play, and assigned values disappear on next domain reload.
- **Rule:** wrap dictionaries in a `[Serializable] SerializableDictionary<K,V>` that stores two parallel `List<K> _keys` and `List<V> _values`, with `ISerializationCallbackReceiver` to rebuild the runtime `Dictionary` on `OnAfterDeserialize`. Validate counts match and reject duplicate keys. Keep it generic-friendly so subclasses give Unity a concrete type for the inspector.

### 3. Interface-typed fields and abstract bases require `[SerializeReference]`
Unity serializes by *declared* type. `[SerializeField] private IFoo _foo;` stores nothing — the concrete subtype info is lost (slicing). Same for `[SerializeField] private abstract Base _b;`.
- **Rule:** add `[SerializeReference]` to a polymorphic field. Unity will then store the concrete type + the field values, restore the right runtime type on load, and let you swap implementations from a custom inspector. `[SerializeReference]` also enables `null` (a default `[SerializeField]` reference-type field reverts to `new T()`, not `null`).

### 4. `[SerializeReference]` on a `List<T>` lets you mix concrete subtypes in one list
A `[SerializeReference] List<IBehaviour> _behaviours;` can contain a `MoveBehaviour`, a `ShootBehaviour`, etc. Each element is serialized with its concrete type tag. The inspector shows the type-picker if you wire a `PropertyDrawer` or use a tool like `SubclassSelectorAttribute`.
- **Pitfall:** every concrete type in the list must be `[Serializable]` AND have a parameterless constructor (Unity creates it via reflection on load). Anonymous types and types with required constructor args don't work.

### 5. Renaming a `[SerializeField]` field orphans the stored value — `[FormerlySerializedAs]` saves it
Unity matches stored data to fields by NAME. Rename `m_speed` to `m_velocity` and every `.asset`/`.prefab`/scene referencing this script loses the value.
- **Rule:** when renaming a serialized field, keep the old name discoverable with `[FormerlySerializedAs("m_speed")]` on the new field for at least one release. After everything's been re-saved, you can remove the attribute. Same applies to `[SerializeReference]` polymorphic class type renames — but those need a `MovedFromAttribute` (or an explicit migration in `OnAfterDeserialize`).

### 6. Enums serialize as integers — reordering or removing values corrupts data
An enum stored as `2` and later you delete the value at index `2` — every saved asset now has the wrong meaning.
- **Rule:** never reorder enum values once shipped. Always add new values at the end. To remove, deprecate with `[Obsolete]` and leave the slot — even if the name becomes a placeholder. Underlying-type enums (`: byte`, `: short`) are also recommended for compact storage when you know the range.

### 7. `ISerializationCallbackReceiver` runs on a background thread during builds
`OnBeforeSerialize` and `OnAfterDeserialize` may execute off the main thread when Unity batches asset serialization. Calling `GameObject.Find`, `Resources.Load`, `Debug.Log`, or anything Unity-API from there can crash or silently corrupt data.
- **Rule:** keep both callbacks to pure data transformation — repopulating internal lists from the dictionary, rebuilding the dictionary from the lists, computing dirty flags. No Unity API, no logging. If you need a Unity-API side effect, defer it (set a flag, react on the next `Update` / `OnEnable`).

### 8. `ScriptableObject` instances created via `CreateInstance` don't persist unless saved as an asset
A `ScriptableObject.CreateInstance<MyData>()` in code is a runtime instance — not saved anywhere. It dies with the next domain reload (or sooner, on garbage collection if you don't hold a reference).
- **Rule:** for runtime data that survives a reload, save the SO as an asset (`AssetDatabase.CreateAsset(so, "Assets/.../foo.asset"); AssetDatabase.SaveAssets();`). For one-off ephemeral data, accept it'll die — and treat it as a temporary aggregate, not durable state.

### 9. Polymorphic data with Unity built-in serialization stops at one level
You can have a polymorphic field via `[SerializeReference]`, but the chosen concrete type's own polymorphic fields need their own `[SerializeReference]`. Unity isn't transitive about this — it's per-field.
- **Rule:** annotate every polymorphic boundary explicitly. If you're modelling a tree of behaviours, every `IBehaviour _child;` inside a concrete `MoveBehaviour` also needs `[SerializeReference]`.

### 10. Sirenix/Odin Serialization extends what's possible, but you trade Unity-native tooling
Odin (Sirenix) serializes anything: dictionaries, generics, interfaces without attributes, custom `ISerializer` types. It's the standard tool for complex polymorphic data. The cost: Odin uses its own binary format stored in a `byte[]` field, which means Unity's text-merge tools see one giant blob, Plastic/Git diffs become useless, and migrations require Odin-aware tooling.
- **Rule:** use Odin when the domain genuinely requires features Unity can't do (multi-key dictionaries, complex generics, polymorphic graphs). For simple structured data — even with one polymorphic field — prefer `[SerializeReference]` + `[Serializable]` so the YAML stays mergeable. Decide per-asset, not per-project.

## Workflow

1. **Sketch the shape, then ask "what does Unity serialize here?"** If you wrote `Dictionary<>` or an interface or an abstract base without `[SerializeReference]`, the data isn't stored — fix it before continuing.
2. **Default to `[SerializeField] private`** for inspector-editable state; the field stays an implementation detail.
3. **Pick a polymorphism strategy upfront.** `[SerializeReference]` for one-level polymorphism, Odin for deep / dictionary-heavy data, plain inheritance + a tag enum if you're ok with no polymorphism in serialization at all.
4. **Before renaming a field**, add `[FormerlySerializedAs("oldName")]` on the new name. Re-save affected assets. Remove the attribute in a later release.
5. **Migration callbacks**: `OnAfterDeserialize` is the place. Detect old shape (e.g. `_keys.Count != _values.Count`) and migrate forward. No Unity API there — defer side effects to `OnEnable` / `OnValidate`.
6. **Verify**: assign a value in the inspector, save, close, reopen the asset. If the value is gone, you have a serialization bug. Repeat after every type / field change in the data layer.

## Quick reference — SerializableDictionary

```csharp
[Serializable]
public class SerializableDictionary<TKey, TValue> : ISerializationCallbackReceiver
{
    [SerializeField] private List<TKey>   _keys   = new List<TKey>();
    [SerializeField] private List<TValue> _values = new List<TValue>();

    [NonSerialized] public Dictionary<TKey, TValue> Map = new Dictionary<TKey, TValue>();

    public void OnBeforeSerialize()
    {
        _keys.Clear(); _values.Clear();
        foreach (var kv in Map) { _keys.Add(kv.Key); _values.Add(kv.Value); }
    }

    public void OnAfterDeserialize()
    {
        Map = new Dictionary<TKey, TValue>(_keys.Count);
        if (_keys.Count != _values.Count)
            throw new SerializationException(
                $"SerializableDictionary key/value count mismatch ({_keys.Count}/{_values.Count}).");
        for (int i = 0; i < _keys.Count; i++) Map[_keys[i]] = _values[i];
    }
}

// Concrete subclass per inspector usage (required — Unity won't show a generic field):
[Serializable] public class StringIntDict : SerializableDictionary<string, int> { }
```

## Quick reference — `[SerializeReference]` polymorphic list

```csharp
[Serializable]
public abstract class BehaviourBase
{
    public string Name;
    public abstract void Tick(float dt);
}

[Serializable] public class MoveBehaviour : BehaviourBase
{
    public Vector3 Velocity;
    public override void Tick(float dt) { /* … */ }
}

[Serializable] public class ShootBehaviour : BehaviourBase
{
    public float Cooldown;
    public override void Tick(float dt) { /* … */ }
}

public class Enemy : MonoBehaviour
{
    [SerializeReference] public List<BehaviourBase> Behaviours = new List<BehaviourBase>();
    // Pair with a SubclassSelector PropertyDrawer in your editor scripts so the inspector
    // shows a "+ MoveBehaviour / ShootBehaviour" dropdown.
}
```

## Related
- `clibridge4unity-prefab-workflow` — how serialized fields propagate across instance overrides and variants
- `clibridge4unity-domain-reload` — what survives via on-disk serialization vs static memory
- `clibridge4unity-editor-tools` — `SerializedProperty` + `serializedObject.ApplyModifiedProperties` for custom inspectors that respect undo / multi-edit
