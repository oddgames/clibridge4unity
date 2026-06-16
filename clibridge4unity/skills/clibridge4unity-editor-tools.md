---
name: clibridge4unity-editor-tools
description: Use for any editor extension — `EditorWindow`, `[CustomEditor]`, `[CustomPropertyDrawer]`, `AssetPostprocessor`, `MenuItem`, scene tools, custom inspectors, batch-edit tooling, IMGUI vs UI Toolkit (USS+UXML), `SerializedObject` / `SerializedProperty` discipline, Scene-view gizmos, Project-window context menus. Auto-trigger on `EditorWindow`, `[CustomEditor]`, `[CustomPropertyDrawer]`, `OnInspectorGUI`, `CreateGUI`, `[MenuItem]`, `AssetPostprocessor`, `OnPostprocessAllAssets`, `SerializedProperty`, `serializedObject.ApplyModifiedProperties`, `Handles.`, `Gizmos.`, `EditorUtility.SetDirty`, `EditorGUILayout` (legacy IMGUI), `UIDocument` in editor, "the inspector doesn't refresh", "undo doesn't work", "edit lost on save". Editor tools fail subtly — Undo / multi-edit / prefab override / play-mode-survival are the common ways your tool silently corrupts.
---

# Unity Editor Tools

Standard Unity editor-tooling discipline (SerializedObject + ApplyModifiedProperties, Undo, SetDirty/MarkSceneDirty, multi-edit, MenuItem validators, AssetPostprocessor filtering, Editor/ folder rules, UI Toolkit CreateGUI/schedule) is assumed knowledge — apply it. Project-specific conventions below.

## House conventions

- **UI Toolkit for new editor windows.** Editor windows use the trio `MyWindow.cs` + `MyWindow.uxml` + `MyWindow.uss`. `CreateGUI()` loads UXML/USS from `Packages/<pkg>/Editor/UI/` (or `Assets/...`), clones the UXML into `rootVisualElement`, registers callbacks. C# only for behaviour (events, data binding) — never `style.color = ...` when a USS class would do.
- **Split complex EditorWindows into Controllers per UXML subtree.** Main window owns lifecycle + composition; each Controller owns its subtree's wiring and data.
- **`delayCall` caveat:** deferring inspector-side work via `EditorApplication.delayCall` is fine (inspector is focused/visible). This is unrelated to — and does NOT override — the project's bridge mandate against `delayCall` for *background/minimized* main-thread marshaling.

## Related
- `clibridge4unity-prefab-workflow` — prefab-instance edit recording
- `clibridge4unity-ui-toolkit` — USS-first patterns, custom UxmlElement controls
- `clibridge4unity-domain-reload` — `[InitializeOnLoad]` setup, first-tick deferred init
- `clibridge4unity-bridge` — `INSPECTOR`, `LOG`, `MENU` to test editor tools from the CLI
