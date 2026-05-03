using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEngine.Events;
using Newtonsoft.Json.Linq;

namespace clibridge4unity
{
    /// <summary>
    /// Commands for manipulating component fields and properties.
    /// </summary>
    public static class ComponentCommands
    {
        /// <summary>
        /// Set a field or property on a component by finding the target GameObject and component.
        /// JSON: {"gameObject":"Path/To/Object", "component":"ComponentName", "field":"fieldName", "value":"Path/To/Target" or value}
        /// </summary>
        [BridgeCommand("COMPONENT_SET", "Set a field or property value on a component",
            Category = "Component",
            Usage = "COMPONENT_SET Canvas/Panel Image m_Color #FF0000\n" +
                    "  COMPONENT_SET {\"gameObject\":\"Canvas/Panel\",\"component\":\"Image\",\"field\":\"m_Color\",\"value\":\"#FF0000\"}",
            RequiresMainThread = true,
            RelatedCommands = new[] { "INSPECTOR" })]
        public static string Set(string jsonData)
        {
            try
            {
                string gameObjectPath;
                string componentName;
                string fieldName;
                JToken valueToken;

                if (jsonData.TrimStart().StartsWith("{"))
                {
                    var data = JObject.Parse(jsonData);
                    gameObjectPath = data["gameObject"]?.ToString();
                    componentName = data["component"]?.ToString();
                    fieldName = data["field"]?.ToString();
                    valueToken = data["value"];
                }
                else
                {
                    // Plain args: COMPONENT_SET <gameObject> <component> <field> <value...>
                    // Supports quoted paths: COMPONENT_SET "Canvas/Text Area/Text" TMP m_fontSize 24
                    var parts = ArgParser.Split(jsonData.Trim(), 4);
                    gameObjectPath = parts.Length > 0 ? parts[0] : null;
                    componentName = parts.Length > 1 ? parts[1] : null;
                    fieldName = parts.Length > 2 ? parts[2] : null;
                    valueToken = parts.Length > 3 ? JToken.FromObject(parts[3]) : null;
                }

                if (string.IsNullOrEmpty(gameObjectPath))
                    return Response.Error("gameObject path is required");
                if (string.IsNullOrEmpty(componentName))
                    return Response.Error("component name is required");
                if (string.IsNullOrEmpty(fieldName))
                    return Response.Error("field name is required");

                // Find the GameObject (scene or prefab asset child)
                bool isPrefab = false;
                GameObject go;
                string prefabAssetPath = null;

                if (gameObjectPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                    gameObjectPath.Contains(".prefab"))
                {
                    go = ResolvePrefabChild(gameObjectPath, out prefabAssetPath);
                    if (go == null)
                    {
                        // Try as whole prefab
                        go = AssetDatabase.LoadAssetAtPath<GameObject>(gameObjectPath);
                    }
                    if (go == null)
                        return Response.Error($"Prefab or child not found: {gameObjectPath}");
                    isPrefab = true;
                }
                else
                {
                    go = GameObject.Find(gameObjectPath);
                    if (go == null)
                        return Response.ErrorSceneNotFound(gameObjectPath);
                }

                // Find the component
                var component = go.GetComponents<Component>()
                    .FirstOrDefault(c => c.GetType().Name.Equals(componentName, StringComparison.OrdinalIgnoreCase));
                if (component == null)
                    return Response.Error($"Component '{componentName}' not found on {gameObjectPath}");

                // Find the field or property
                var componentType = component.GetType();
                var field = componentType.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var property = componentType.GetProperty(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (field == null && property == null)
                    return Response.Error($"Field or property '{fieldName}' not found on component {componentName}");

                // Determine the target type
                Type targetType = field?.FieldType ?? property?.PropertyType;

                // Parse the value
                object value = ParseValue(valueToken, targetType);

                // Set the value
                Undo.RecordObject(component, $"Set {fieldName}");
                if (field != null)
                    field.SetValue(component, value);
                else
                    property.SetValue(component, value);

                EditorUtility.SetDirty(component);

                // Save prefab asset if we modified a prefab
                if (isPrefab && !string.IsNullOrEmpty(prefabAssetPath))
                    AssetDatabase.SaveAssets();

                return Response.SuccessWithData(new
                {
                    gameObject = gameObjectPath,
                    component = componentName,
                    field = fieldName,
                    value = value?.ToString() ?? "null"
                });
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Add a component to a GameObject.
        /// </summary>
        [BridgeCommand("COMPONENT_ADD", "Add a component to a GameObject",
            Category = "Component",
            Usage = "COMPONENT_ADD Canvas/Panel BoxCollider\n" +
                    "  COMPONENT_ADD {\"gameObject\":\"Canvas/Panel\",\"component\":\"BoxCollider\"}",
            RequiresMainThread = true,
            RelatedCommands = new[] { "INSPECTOR", "COMPONENT_SET" })]
        public static string AddComponent(string jsonData)
        {
            try
            {
                string gameObjectPath, componentName;
                if (jsonData.TrimStart().StartsWith("{"))
                {
                    var data = JObject.Parse(jsonData);
                    gameObjectPath = data["gameObject"]?.ToString();
                    componentName = data["component"]?.ToString();
                }
                else
                {
                    // Plain args: COMPONENT_ADD <gameObject> <component>
                    var parts = ArgParser.Split(jsonData.Trim(), 2);
                    gameObjectPath = parts.Length > 0 ? parts[0] : null;
                    componentName = parts.Length > 1 ? parts[1] : null;
                }

                if (string.IsNullOrEmpty(gameObjectPath))
                    return Response.Error("gameObject path is required");
                if (string.IsNullOrEmpty(componentName))
                    return Response.Error("component name is required");

                bool isPrefab = false;
                string prefabAssetPath = null;
                GameObject go;

                if (gameObjectPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                    gameObjectPath.Contains(".prefab"))
                {
                    go = ResolvePrefabChild(gameObjectPath, out prefabAssetPath)
                      ?? AssetDatabase.LoadAssetAtPath<GameObject>(gameObjectPath);
                    if (go == null)
                        return Response.Error($"Prefab or child not found: {gameObjectPath}");
                    isPrefab = true;
                }
                else
                {
                    go = GameObject.Find(gameObjectPath);
                    if (go == null)
                        return Response.ErrorSceneNotFound(gameObjectPath);
                }

                var componentType = FindType(componentName);
                if (componentType == null)
                    return Response.Error($"Component type not found: {componentName}");

                if (!typeof(Component).IsAssignableFrom(componentType))
                    return Response.Error($"Type '{componentName}' is not a Component");

                Undo.RecordObject(go, $"Add {componentName}");
                var added = go.AddComponent(componentType);
                EditorUtility.SetDirty(go);
                if (isPrefab) AssetDatabase.SaveAssets();

                return Response.Success($"Added {added.GetType().Name} to {gameObjectPath}");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Remove a component from a GameObject.
        /// </summary>
        [BridgeCommand("COMPONENT_REMOVE", "Remove a component from a GameObject",
            Category = "Component",
            Usage = "COMPONENT_REMOVE Canvas/Panel BoxCollider\n" +
                    "  COMPONENT_REMOVE {\"gameObject\":\"Canvas/Panel\",\"component\":\"BoxCollider\"}",
            RequiresMainThread = true,
            RelatedCommands = new[] { "INSPECTOR" })]
        public static string RemoveComponent(string jsonData)
        {
            try
            {
                string gameObjectPath, componentName;
                if (jsonData.TrimStart().StartsWith("{"))
                {
                    var data = JObject.Parse(jsonData);
                    gameObjectPath = data["gameObject"]?.ToString();
                    componentName = data["component"]?.ToString();
                }
                else
                {
                    var parts = ArgParser.Split(jsonData.Trim(), 2);
                    gameObjectPath = parts.Length > 0 ? parts[0] : null;
                    componentName = parts.Length > 1 ? parts[1] : null;
                }

                if (string.IsNullOrEmpty(gameObjectPath))
                    return Response.Error("gameObject path is required");
                if (string.IsNullOrEmpty(componentName))
                    return Response.Error("component name is required");

                bool isPrefab = false;
                string prefabAssetPath = null;
                GameObject go;

                if (gameObjectPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                    gameObjectPath.Contains(".prefab"))
                {
                    go = ResolvePrefabChild(gameObjectPath, out prefabAssetPath)
                      ?? AssetDatabase.LoadAssetAtPath<GameObject>(gameObjectPath);
                    if (go == null)
                        return Response.Error($"Prefab or child not found: {gameObjectPath}");
                    isPrefab = true;
                }
                else
                {
                    go = GameObject.Find(gameObjectPath);
                    if (go == null)
                        return Response.ErrorSceneNotFound(gameObjectPath);
                }

                var component = go.GetComponents<Component>()
                    .FirstOrDefault(c => c.GetType().Name.Equals(componentName, StringComparison.OrdinalIgnoreCase));
                if (component == null)
                    return Response.Error($"Component '{componentName}' not found on {gameObjectPath}");

                Undo.DestroyObjectImmediate(component);
                EditorUtility.SetDirty(go);
                if (isPrefab) AssetDatabase.SaveAssets();

                return Response.Success($"Removed {componentName} from {gameObjectPath}");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Unified inspector — works on scene, prefab assets, materials, ScriptableObjects, etc.
        /// Absorbs the former PREFAB_HIERARCHY command via --brief + --filter + truncation.
        /// </summary>
        [BridgeCommand("INSPECTOR", "Inspect a scene GameObject, prefab asset, material, ScriptableObject — optionally a subtree with filter",
            Category = "Component",
            Usage = "INSPECTOR                                             (scene hierarchy — all roots, brief)\n" +
                    "  INSPECTOR scene                                     (same — explicit)\n" +
                    "  INSPECTOR Canvas/Panel                              (one scene GameObject with fields)\n" +
                    "  INSPECTOR Canvas/Panel --depth 2                    (recurse 2 levels)\n" +
                    "  INSPECTOR Canvas/Panel --children                   (recurse all children)\n" +
                    "  INSPECTOR Canvas --filter Button                    (subtree, keep only nodes matching 'Button' by GO or component name)\n" +
                    "  INSPECTOR Canvas --brief                            (components only, skip serialized fields)\n" +
                    "  INSPECTOR Assets/Prefabs/My.prefab                  (prefab asset)\n" +
                    "  INSPECTOR Assets/Prefabs/My.prefab --children       (full prefab subtree with fields)\n" +
                    "  INSPECTOR Assets/Prefabs/My.prefab --children --brief --filter Button  (prefab, subtree, components-only, filtered)\n" +
                    "  INSPECTOR Assets/Materials/My.mat                   (material)\n" +
                    "  INSPECTOR {\"gameObject\":\"Panel\",\"filter\":\"Button\",\"children\":true,\"brief\":true}  (JSON form)",
            RequiresMainThread = true,
            RelatedCommands = new[] { "COMPONENT_SET", "COMPONENT_ADD", "SCREENSHOT", "FIND" })]
        public static string Inspector(string data)
        {
            try
            {
                var opts = ParseInspectorOptions(data);

                // No target → scene-wide (replaces the former SCENE command's hierarchy output).
                bool sceneScope = string.IsNullOrEmpty(opts.TargetPath) ||
                                  opts.TargetPath.Equals("scene", StringComparison.OrdinalIgnoreCase);
                if (sceneScope)
                {
                    // Default scene scope to brief + all children.
                    if (opts.Depth == 0) opts.Depth = int.MaxValue;
                    if (!opts.BriefExplicit) opts.Brief = true;
                    return InspectScene(opts);
                }

                // Asset path → inspect asset (prefab / material / SO / etc).
                if (opts.TargetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                 || opts.TargetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                {
                    if (!EditorApplication.isCompiling && !EditorApplication.isUpdating)
                        AssetSyncHelper.EnsureSynced(opts.TargetPath);
                    return InspectAsset(opts);
                }

                // Scene GameObject
                var go = GameObject.Find(opts.TargetPath);
                if (go == null)
                    return Response.ErrorSceneNotFound(opts.TargetPath);

                var sb = new System.Text.StringBuilder();
                var counter = new InspectorNodeCounter { MaxNodes = opts.MaxNodes };
                AppendGameObjectInspection(sb, go, opts, 0, opts.TargetPath, counter);
                if (counter.Truncated)
                    sb.AppendLine($"... (truncated at {opts.MaxNodes} nodes — use --filter or deeper path to narrow)");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        private class InspectorOptions
        {
            public string TargetPath;
            public string Filter;           // matches GameObject name OR component name (substring, case-insensitive)
            public string FilterComponent;  // exact component type name (legacy JSON "component" field)
            public int Depth;
            public bool Brief;              // components only, skip serialized fields
            public bool BriefExplicit;      // user set Brief explicitly (vs default)
            public int MaxNodes = 300;
        }

        private class InspectorNodeCounter
        {
            public int Count;
            public int MaxNodes;
            public bool Truncated;
        }

        private static InspectorOptions ParseInspectorOptions(string data)
        {
            var opts = new InspectorOptions();
            if (string.IsNullOrWhiteSpace(data)) return opts;

            if (data.TrimStart().StartsWith("{"))
            {
                var json = JObject.Parse(data);
                opts.TargetPath = json["gameObject"]?.ToString() ?? json["asset"]?.ToString() ?? json["target"]?.ToString();
                opts.FilterComponent = json["component"]?.ToString();
                opts.Filter = json["filter"]?.ToString();
                var depthToken = json["depth"];
                if (depthToken != null) opts.Depth = depthToken.ToObject<int>();
                if (json["children"]?.ToObject<bool>() == true) opts.Depth = int.MaxValue;
                var briefToken = json["brief"];
                if (briefToken != null) { opts.Brief = briefToken.ToObject<bool>(); opts.BriefExplicit = true; }
                var maxToken = json["maxNodes"];
                if (maxToken != null) opts.MaxNodes = maxToken.ToObject<int>();
                return opts;
            }

            var tokens = data.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var pathParts = new System.Collections.Generic.List<string>();
            for (int i = 0; i < tokens.Length; i++)
            {
                string t = tokens[i];
                if (t.Equals("--children", StringComparison.OrdinalIgnoreCase))
                    opts.Depth = int.MaxValue;
                else if (t.Equals("--depth", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Length
                      && int.TryParse(tokens[i + 1], out int d))
                { opts.Depth = Math.Max(0, d); i++; }
                else if (t.Equals("--filter", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Length)
                { opts.Filter = tokens[++i]; }
                else if (t.Equals("--brief", StringComparison.OrdinalIgnoreCase) ||
                         t.Equals("--components-only", StringComparison.OrdinalIgnoreCase))
                { opts.Brief = true; opts.BriefExplicit = true; }
                else if (t.Equals("--max", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Length
                      && int.TryParse(tokens[i + 1], out int m))
                { opts.MaxNodes = Math.Max(1, m); i++; }
                else
                    pathParts.Add(t);
            }
            opts.TargetPath = string.Join(" ", pathParts);
            return opts;
        }

        /// <summary>Walk all roots of the active scene, apply filter + depth, render. Replaces SCENE.</summary>
        private static string InspectScene(InspectorOptions opts)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Scene: {scene.name}  ({roots.Length} root{(roots.Length == 1 ? "" : "s")}, total {UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length} objects)");
            if (!string.IsNullOrEmpty(opts.Filter))
                sb.AppendLine($"Filter: '{opts.Filter}'");
            sb.AppendLine();

            var counter = new InspectorNodeCounter { MaxNodes = opts.MaxNodes };
            foreach (var root in roots)
            {
                AppendGameObjectInspection(sb, root, opts, 0, root.name, counter);
                if (counter.Truncated) break;
            }
            if (counter.Truncated)
                sb.AppendLine($"... (truncated at {opts.MaxNodes} nodes — use --filter or a specific path to narrow)");
            return sb.ToString().TrimEnd();
        }

        /// <summary>Count transforms in a subtree (including root).</summary>
        private static int CountTransforms(Transform t)
        {
            int n = 1;
            for (int i = 0; i < t.childCount; i++) n += CountTransforms(t.GetChild(i));
            return n;
        }

        /// <summary>
        /// Render a GameObject and (optionally) its descendants up to opts.Depth levels deep.
        /// Honors --filter (name OR component), --brief (skip fields), --max (node cap).
        /// </summary>
        private static void AppendGameObjectInspection(System.Text.StringBuilder sb, GameObject go,
            InspectorOptions opts, int currentDepth, string displayPath, InspectorNodeCounter counter)
        {
            if (counter.Truncated) return;

            var components = go.GetComponents<Component>();

            // Filter decides whether THIS node is rendered. If it doesn't match,
            // still recurse — a descendant might.
            bool nodeMatches = MatchesFilter(go, components, opts);

            if (nodeMatches)
            {
                if (counter.Count >= counter.MaxNodes) { counter.Truncated = true; return; }
                counter.Count++;

                string indent = new string(' ', currentDepth * 2);
                sb.AppendLine($"{indent}GameObject: {displayPath ?? go.name}");
                sb.AppendLine($"{indent}active: {go.activeSelf}  layer: {LayerMask.LayerToName(go.layer)}  tag: {go.tag}");

                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;

                    // Legacy exact-component filter from JSON form.
                    if (!string.IsNullOrEmpty(opts.FilterComponent) &&
                        !typeName.Equals(opts.FilterComponent, StringComparison.OrdinalIgnoreCase))
                        continue;

                    sb.AppendLine($"{indent}[{typeName}]");
                    if (opts.Brief) continue;  // components-only mode — skip serialized fields

                    var so = new SerializedObject(comp);
                    var prop = so.GetIterator();
                    if (prop.NextVisible(true))
                    {
                        do
                        {
                            sb.AppendLine($"{indent}  {prop.name}: {GetPropertyValue(prop)}");
                        } while (prop.NextVisible(false));
                    }
                }
            }

            if (currentDepth >= opts.Depth) return;

            foreach (Transform child in go.transform)
            {
                if (counter.Truncated) break;
                if (nodeMatches) sb.AppendLine();
                AppendGameObjectInspection(sb, child.gameObject, opts,
                    currentDepth + 1, child.name, counter);
            }
        }

        /// <summary>Filter test: no filter → always true; else GO name or any component name contains the filter (case-insensitive).</summary>
        private static bool MatchesFilter(GameObject go, Component[] components, InspectorOptions opts)
        {
            if (string.IsNullOrEmpty(opts.Filter)) return true;
            if (go.name.IndexOf(opts.Filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var comp in components)
            {
                if (comp == null) continue;
                if (comp.GetType().Name.IndexOf(opts.Filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static string InspectAsset(InspectorOptions opts)
        {
            string assetPath = opts.TargetPath;

            // Support child paths: Assets/My.prefab/Child/Path
            string childPath = null;
            int prefabIdx = assetPath.IndexOf(".prefab/", StringComparison.OrdinalIgnoreCase);
            if (prefabIdx >= 0)
            {
                childPath = assetPath.Substring(prefabIdx + ".prefab/".Length);
                assetPath = assetPath.Substring(0, prefabIdx + ".prefab".Length);
            }

            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
                return Response.ErrorAssetNotFound(assetPath);

            // Prefab → inspect like a GameObject (components + optional subtree)
            if (asset is GameObject go)
            {
                // Navigate to child if specified
                if (!string.IsNullOrEmpty(childPath))
                {
                    var child = go.transform.Find(childPath);
                    if (child == null)
                    {
                        // Try recursive search by name
                        child = FindChildRecursive(go.transform, childPath);
                    }
                    if (child == null)
                    {
                        var sb2 = new System.Text.StringBuilder();
                        sb2.AppendLine($"Child not found: {childPath}");
                        sb2.AppendLine($"Available children of {go.name}:");
                        ListChildren(go.transform, sb2, 2);
                        return Response.Error(sb2.ToString().TrimEnd());
                    }
                    go = child.gameObject;
                    assetPath = $"{assetPath}/{childPath}";
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Prefab: {assetPath}");
                var prefabType = PrefabUtility.GetPrefabAssetType(go);
                sb.AppendLine($"prefabType: {prefabType}");
                if (prefabType == PrefabAssetType.Variant)
                {
                    var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (source != null)
                        sb.AppendLine($"basePrefab: {AssetDatabase.GetAssetPath(source)}");
                }
                int totalNodes = CountTransforms(go.transform);
                sb.AppendLine($"nodes: {totalNodes}");
                if (!string.IsNullOrEmpty(opts.Filter))
                    sb.AppendLine($"filter: '{opts.Filter}'");
                sb.AppendLine();

                var counter = new InspectorNodeCounter { MaxNodes = opts.MaxNodes };
                AppendGameObjectInspection(sb, go, opts, 0, go.name, counter);
                if (counter.Truncated)
                    sb.AppendLine($"... (truncated at {opts.MaxNodes} nodes — use --filter or deeper path to narrow)");
                return sb.ToString().TrimEnd();
            }

            // Any other asset (ScriptableObject, Material, Shader, Texture, AudioClip, etc.)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Asset: {assetPath}");
                sb.AppendLine($"type: {asset.GetType().Name}");
                sb.AppendLine($"name: {asset.name}");

                // Sub-assets (e.g. FBX contains meshes, materials, clips)
                var subAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
                if (subAssets.Length > 1)
                {
                    var subs = subAssets.Where(s => s != null && s != asset).ToList();
                    sb.AppendLine($"subAssets: {subs.Count}");
                    foreach (var sub in subs.Take(20))
                        sb.AppendLine($"  [{sub.GetType().Name}] {sub.name}");
                    if (subs.Count > 20)
                        sb.AppendLine($"  ... +{subs.Count - 20} more");
                }
                sb.AppendLine("---");

                // Serialized properties
                sb.AppendLine($"[{asset.GetType().Name}]");
                var serialized = new SerializedObject(asset);
                var iter = serialized.GetIterator();
                if (iter.NextVisible(true))
                {
                    do
                    {
                        sb.AppendLine($"  {iter.name}: {GetPropertyValue(iter)}");
                    } while (iter.NextVisible(false));
                }

                return sb.ToString().TrimEnd();
            }
        }

        private static string GetPropertyValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: return prop.intValue.ToString();
                case SerializedPropertyType.Boolean: return prop.boolValue.ToString();
                case SerializedPropertyType.Float: return prop.floatValue.ToString("G");
                case SerializedPropertyType.String: return prop.stringValue;
                case SerializedPropertyType.Enum: return prop.enumDisplayNames.Length > prop.enumValueIndex && prop.enumValueIndex >= 0
                    ? prop.enumDisplayNames[prop.enumValueIndex] : prop.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference:
                    return prop.objectReferenceValue != null ? prop.objectReferenceValue.name : "None";
                case SerializedPropertyType.Vector2: return prop.vector2Value.ToString();
                case SerializedPropertyType.Vector3: return prop.vector3Value.ToString();
                case SerializedPropertyType.Vector4: return prop.vector4Value.ToString();
                case SerializedPropertyType.Rect: return prop.rectValue.ToString();
                case SerializedPropertyType.Color: return prop.colorValue.ToString();
                case SerializedPropertyType.LayerMask: return prop.intValue.ToString();
                default: return $"({prop.propertyType})";
            }
        }

        private static Type FindType(string name)
        {
            // Try common Unity namespaces first
            var type = Type.GetType($"UnityEngine.{name}, UnityEngine")
                    ?? Type.GetType($"UnityEngine.UI.{name}, UnityEngine.UI")
                    ?? Type.GetType($"UnityEngine.{name}, UnityEngine.CoreModule");

            if (type != null) return type;

            // Search all loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetTypes().FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (type != null) return type;
            }
            return null;
        }

        private static object ParseValue(JToken valueToken, Type targetType)
        {
            if (valueToken == null || valueToken.Type == JTokenType.Null)
                return null;

            // If target is a Unity Object (GameObject, Component, etc.)
            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                string path = valueToken.ToString();

                // Try to find as GameObject first
                var go = GameObject.Find(path);
                if (go != null)
                {
                    // If target type is GameObject, return it
                    if (targetType == typeof(GameObject))
                        return go;

                    // If target type is a Component, try to get it
                    if (typeof(Component).IsAssignableFrom(targetType))
                    {
                        var component = go.GetComponent(targetType);
                        if (component != null)
                            return component;
                    }
                }

                // Try to load as asset
                var asset = AssetDatabase.LoadAssetAtPath(path, targetType);
                if (asset != null)
                    return asset;

                return null;
            }

            // For primitive types, convert the token
            if (targetType == typeof(string))
                return valueToken.ToString();
            if (targetType == typeof(int))
                return valueToken.ToObject<int>();
            if (targetType == typeof(float))
                return valueToken.ToObject<float>();
            if (targetType == typeof(bool))
                return valueToken.ToObject<bool>();
            if (targetType == typeof(double))
                return valueToken.ToObject<double>();

            // For other types, try to deserialize
            return valueToken.ToObject(targetType);
        }

        /// <summary>
        /// Resolves a prefab asset child path like "Assets/My.prefab/Child/Path"
        /// into the actual GameObject. Returns null if not a prefab path.
        /// </summary>
        private static GameObject ResolvePrefabChild(string path, out string assetPath)
        {
            assetPath = null;
            int prefabIdx = path.IndexOf(".prefab/", StringComparison.OrdinalIgnoreCase);
            if (prefabIdx < 0) return null;

            string childPath = path.Substring(prefabIdx + ".prefab/".Length);
            assetPath = path.Substring(0, prefabIdx + ".prefab".Length);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null) return null;

            var child = prefab.transform.Find(childPath);
            if (child == null) child = FindChildRecursive(prefab.transform, childPath);
            return child?.gameObject;
        }

        private static Transform FindChildRecursive(Transform parent, string name)
        {
            if (name.Contains("/"))
            {
                var found = parent.Find(name);
                if (found != null) return found;
            }
            foreach (Transform child in parent)
            {
                if (child.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return child;
                var result = FindChildRecursive(child, name);
                if (result != null) return result;
            }
            return null;
        }

        private static void ListChildren(Transform parent, System.Text.StringBuilder sb, int maxDepth, int depth = 0)
        {
            string indent = new string(' ', depth * 2 + 2);
            foreach (Transform child in parent)
            {
                sb.AppendLine($"{indent}{child.name}");
                if (depth < maxDepth)
                    ListChildren(child, sb, maxDepth, depth + 1);
            }
        }
    }
}
