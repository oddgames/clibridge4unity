---
name: clibridge4unity-editor-tools
description: Use for any editor extension — `EditorWindow`, `[CustomEditor]`, `[CustomPropertyDrawer]`, `AssetPostprocessor`, `MenuItem`, scene tools, custom inspectors, batch-edit tooling, IMGUI vs UI Toolkit (USS+UXML), `SerializedObject` / `SerializedProperty` discipline, Scene-view gizmos, Project-window context menus. Auto-trigger on `EditorWindow`, `[CustomEditor]`, `[CustomPropertyDrawer]`, `OnInspectorGUI`, `CreateGUI`, `[MenuItem]`, `AssetPostprocessor`, `OnPostprocessAllAssets`, `SerializedProperty`, `serializedObject.ApplyModifiedProperties`, `Handles.`, `Gizmos.`, `EditorUtility.SetDirty`, `EditorGUILayout` (legacy IMGUI), `UIDocument` in editor, "the inspector doesn't refresh", "undo doesn't work", "edit lost on save". Editor tools fail subtly — Undo / multi-edit / prefab override / play-mode-survival are the common ways your tool silently corrupts.
---

# Unity Editor Tools

Editor tooling has two parts: the UI (IMGUI legacy or UI Toolkit modern) and the data discipline (`SerializedObject` + `Undo` + dirty-marking). Get the UI wrong and the tool looks ugly; get the data discipline wrong and the user loses work. This skill is about the data discipline first, the UI second.

## The one rule that prevents most of these

**Mutate via `SerializedObject` + `serializedObject.ApplyModifiedProperties()`, never via direct field assignment.** ApplyModifiedProperties records an undo step, dirties the right object (asset or prefab instance override), propagates to multi-edit selections, and triggers `OnValidate`. Direct `target.field = value` skips all of it — the inspector shows the change, the save drops it.

## Pitfall catalog

### 1. Direct field assignment in custom inspector loses on save
`((MyComponent)target).speed = 5f` in `OnInspectorGUI` changes the field in memory. Unity's serialization doesn't notice; the prefab/asset/scene save drops the value back to the stored one.
- **Rule:** always use `serializedObject.FindProperty(...)` + `EditorGUI.PropertyField` (or set `.floatValue` / `.intValue` directly on the property), wrapped in `serializedObject.Update()` at the top and `serializedObject.ApplyModifiedProperties()` at the bottom of `OnInspectorGUI`. If you must skip `SerializedObject` for an edge case, follow up with `EditorUtility.SetDirty(target)` + `PrefabUtility.RecordPrefabInstancePropertyModifications(target)` for prefab instances.

### 2. Multi-edit silently corrupts when you skip SerializedObject
`[CustomEditor(typeof(MyComponent))]` without `[CanEditMultipleObjects]` blocks multi-select editing. Adding the attribute without using `SerializedObject` lets the user select 5 objects, change a field, save — and only the *first* object gets the change.
- **Rule:** add `[CanEditMultipleObjects]` AND use `serializedObject` throughout. The SerializedProperty knows how to spread the edit across all selected targets. `target` (singular) is the first one; `targets` (plural) is the array.

### 3. Editor changes without `Undo.RecordObject` are gone after Ctrl+Z
A user expects every editor-mutation to be undoable. A tool that changes 100 fields with no `Undo.RecordObject` leaves the user unable to revert without manually restoring the asset from version control.
- **Rule:** `Undo.RecordObject(target, "human-readable label")` before each mutation (or `Undo.RecordObjects(targets, "label")` for arrays). For instantiation, `Undo.RegisterCreatedObjectUndo(newGo, "label")`. For removing components, `Undo.DestroyObjectImmediate(component)`. Match the API to the operation.

### 4. `EditorUtility.SetDirty` on a scene object doesn't save the scene — `MarkSceneDirty` does
`SetDirty` works on assets and ScriptableObjects. On a scene GameObject's component, you also need `EditorSceneManager.MarkSceneDirty(scene)` (or `EditorUtility.SetDirty` + `MarkSceneDirty`). Otherwise Unity sees the scene as clean and the next save drops your changes.
- **Rule:** at the end of any tool that mutates scene objects, call `EditorSceneManager.MarkSceneDirty(go.scene)` for each affected scene. Prefab instances also need `PrefabUtility.RecordPrefabInstancePropertyModifications(target)` so the override is captured.

### 5. `OnInspectorGUI` runs many times per frame — keep it cheap
The custom inspector renders on every Layout AND every Repaint event, plus on every input event. Allocating a list, reading a heavy file, or scanning the project from within `OnInspectorGUI` runs hundreds of times per second when the user has the inspector open and is moving the mouse.
- **Rule:** cache anything expensive in instance fields populated in `OnEnable`. Refresh on `OnValidate` or via a manual "Refresh" button. Heavy work via `EditorApplication.delayCall` after the GUI loop exits.

### 6. `[MenuItem]` validator overloads — keep your menu items disabled when they don't apply
Adding a second method with `[MenuItem("Foo", validate = true)]` lets you return `false` to grey out the menu when conditions aren't met. Skipping the validator means the menu fires even on invalid selections; users hit a hard error or a no-op.
- **Rule:** every `[MenuItem]` with selection-dependent behaviour has a validator overload. `Selection.activeGameObject != null` for "needs a GameObject," `PrefabStageUtility.GetCurrentPrefabStage() != null` for "only in prefab mode," etc.

### 7. UI Toolkit (USS+UXML) is the path forward for new editor windows — IMGUI is legacy
IMGUI (`OnGUI` / `EditorGUILayout` / `GUILayout`) is the original immediate-mode API. UI Toolkit (`CreateGUI` + `rootVisualElement` + UXML/USS) is the retained-mode replacement Unity is investing in. New windows should use UI Toolkit; existing IMGUI windows are fine to leave (rewriting is rarely justified by ROI alone).
- **Real convention:** Editor windows use the trio `MyWindow.cs` + `MyWindow.uxml` + `MyWindow.uss`. `CreateGUI()` loads the UXML/USS from `Packages/<pkg>/Editor/UI/` (or `Assets/...`), clones the UXML into `rootVisualElement`, and registers callbacks. Complex windows split into Controllers per UXML subtree.
- **Rule:** new editor windows → UI Toolkit. Use UXML for structure, USS for styling, C# only for behaviour (events, data binding). Never `style.color = ...` in C# when a USS class would do.

### 8. UI Toolkit windows refresh via `schedule.Execute(...).Every(ms)`, NOT `EditorApplication.update`
`EditorApplication.update` fires only when Unity has focus and isn't backgrounded. `VisualElement.schedule.Execute(callback).Every(milliseconds)` ticks reliably as long as the window is open.
- **Rule:** for periodic refresh in a UI Toolkit window, use the `schedule` API. For one-off deferred work, `schedule.Execute(callback).StartingIn(ms)`. Never `EditorApplication.update += MyTick` — that's an IMGUI-era pattern.

### 9. `AssetPostprocessor.OnPostprocessAllAssets` runs on every import — guard early
Override `static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)` and you get called for every asset Unity imports. Big projects: thousands of calls during package install. Doing expensive work on every call freezes the editor.
- **Real pattern:** filter the input arrays first (`.Where(p => p.EndsWith(".json"))`), skip work if no relevant files, batch the rest into a single pass.
- **Rule:** every `OnPostprocessAllAssets` filters by file type / path prefix as the first action. If no matches, return immediately. Don't iterate every `.cs`/`.png`/`.meta`/`.asmdef` looking for the one you care about.

### 10. Editor scripts must live under an `Editor/` folder (or in an Editor-only asmdef)
A script using `UnityEditor.*` APIs in a runtime folder fails the player build with "namespace UnityEditor does not exist." The fix is `#if UNITY_EDITOR` guards (for small bits) or moving the file to an Editor-only folder (for whole files).
- **Rule:** wholesale editor scripts → `Assets/.../Editor/` folder, asmdef with `Editor` platform only. Mixed runtime/editor types → `#if UNITY_EDITOR ... #endif` around the editor-only members. Cross-asmdef editor-only utilities → make a `MyPackage.Editor` asmdef referencing the runtime one.

## Workflow

1. **Decide IMGUI vs UI Toolkit.** New windows → UI Toolkit. Tiny one-off inspector tweaks → IMGUI is fine if everything else in the file already uses it. Don't mix in one window.
2. **Data discipline first.** `SerializedObject` + `serializedObject.ApplyModifiedProperties` + `Undo.RecordObject` are non-negotiable for any mutation. Skip them and your tool silently loses work.
3. **`OnEnable`/`CreateGUI` for setup**, `OnDisable`/`OnDestroy` for cleanup (subscriptions, file handles).
4. **Menu validators** for any selection-dependent action.
5. **Test the round-trip.** Make a change, save, close, reopen, verify. Then close without saving, reopen, verify the change is correctly NOT there. Also test multi-select.
6. **For complex EditorWindows**, split into Controllers per UXML subtree. Main window owns lifecycle + composition; each Controller owns its subtree's wiring and data.

## Quick reference — minimal custom editor

```csharp
[CustomEditor(typeof(MyComponent)), CanEditMultipleObjects]
public class MyComponentEditor : Editor
{
    SerializedProperty _speedProp;

    void OnEnable() => _speedProp = serializedObject.FindProperty("_speed");

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EditorGUILayout.PropertyField(_speedProp);
        if (serializedObject.ApplyModifiedProperties()) { /* commit + record undo + dirty */ }
    }
}
```

## Quick reference — UI Toolkit EditorWindow skeleton

```csharp
public class MyWindow : EditorWindow
{
    private const string UxmlPath = "Packages/com.foo.tools/Editor/UI/MyWindow.uxml";
    private const string UssPath  = "Packages/com.foo.tools/Editor/UI/MyWindow.uss";

    [MenuItem("Tools/My Window")]
    public static void Open() => GetWindow<MyWindow>("My Window");

    private void CreateGUI()
    {
        var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
        rootVisualElement.styleSheets.Add(uss);
        uxml.CloneTree(rootVisualElement);

        var refreshBtn = rootVisualElement.Q<Button>("refresh");
        refreshBtn.clicked += Refresh;

        rootVisualElement.schedule.Execute(Refresh).Every(1000); // periodic
    }

    private void Refresh() { /* … */ }
}
```

## Quick reference — safe AssetPostprocessor

```csharp
class JsonBeautifyImporter : AssetPostprocessor
{
    static void OnPostprocessAllAssets(string[] imported, string[] _, string[] __, string[] ___)
    {
        foreach (var path in imported)
        {
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            // Beautify, skipping files already formatted to avoid reimport loops.
        }
    }
}
```

## Related
- `clibridge4unity-prefab-workflow` — `SerializedObject` + `PrefabUtility.RecordPrefabInstancePropertyModifications` for prefab-instance edits
- `clibridge4unity-ui-toolkit` — USS-first patterns + custom UxmlElement controls for retained-mode UI
- `clibridge4unity-domain-reload` — `[InitializeOnLoad]` setup pattern + `EditorApplication.update` for first-tick deferred init
- `clibridge4unity-bridge` — `INSPECTOR`, `LOG`, `MENU` commands for testing editor tools from the CLI
