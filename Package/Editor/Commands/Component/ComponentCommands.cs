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
                        return Response.Error($"GameObject not found: {gameObjectPath}");
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
                        return Response.Error($"GameObject not found: {gameObjectPath}");
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
                        return Response.Error($"GameObject not found: {gameObjectPath}");
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
        /// Inspect a GameObject's components and their serialized fields.
        /// </summary>
        [BridgeCommand("INSPECTOR", "Inspect a scene GameObject or any asset (prefab, material, shader, ScriptableObject, etc.)",
            Category = "Component",
            Usage = "INSPECTOR Canvas/Panel                              (scene GameObject)\n" +
                    "  INSPECTOR Canvas/Panel --depth 2                    (recurse 2 levels)\n" +
                    "  INSPECTOR Canvas/Panel --children                   (recurse all children)\n" +
                    "  INSPECTOR Assets/Prefabs/My.prefab                  (prefab asset)\n" +
                    "  INSPECTOR Assets/Materials/My.mat                   (material)\n" +
                    "  INSPECTOR Assets/Data/Config.asset                  (ScriptableObject)\n" +
                    "  INSPECTOR {\"gameObject\":\"Panel\",\"component\":\"Image\"}  (filter component)\n" +
                    "  INSPECTOR {\"gameObject\":\"Panel\",\"depth\":2}         (JSON form)",
            RequiresMainThread = true,
            RelatedCommands = new[] { "COMPONENT_SET", "COMPONENT_ADD", "SCREENSHOT" })]
        public static string Inspector(string data)
        {
            try
            {
                string targetPath;
                string filterComponent = null;
                int depth = 0;

                if (data.TrimStart().StartsWith("{"))
                {
                    var json = JObject.Parse(data);
                    targetPath = json["gameObject"]?.ToString() ?? json["asset"]?.ToString();
                    filterComponent = json["component"]?.ToString();
                    var depthToken = json["depth"];
                    if (depthToken != null) depth = depthToken.ToObject<int>();
                    if (json["children"]?.ToObject<bool>() == true) depth = int.MaxValue;
                }
                else
                {
                    targetPath = ParseInspectorFlags(data.Trim(), out depth);
                }

                if (string.IsNullOrEmpty(targetPath))
                    return Response.Error("Path is required");

                // Asset path → inspect asset directly
                if (targetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                 || targetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                {
                    return InspectAsset(targetPath, filterComponent);
                }

                // Scene GameObject
                var go = GameObject.Find(targetPath);
                if (go == null)
                    return Response.Error($"GameObject not found: {targetPath}");

                var sb = new System.Text.StringBuilder();
                AppendGameObjectInspection(sb, go, filterComponent, depth, 0, targetPath);
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Strip trailing `--depth N` / `--children` flags from the data string and return the bare path.
        /// </summary>
        private static string ParseInspectorFlags(string input, out int depth)
        {
            depth = 0;
            if (string.IsNullOrEmpty(input)) return input;

            var tokens = input.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var pathParts = new System.Collections.Generic.List<string>();
            for (int i = 0; i < tokens.Length; i++)
            {
                string t = tokens[i];
                if (t.Equals("--children", StringComparison.OrdinalIgnoreCase))
                {
                    depth = int.MaxValue;
                }
                else if (t.Equals("--depth", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Length
                      && int.TryParse(tokens[i + 1], out int d))
                {
                    depth = Math.Max(0, d);
                    i++;
                }
                else
                {
                    pathParts.Add(t);
                }
            }
            return string.Join(" ", pathParts);
        }

        /// <summary>
        /// Render a GameObject and (optionally) its descendants up to `maxDepth` levels deep.
        /// Indent by `currentDepth * 2` spaces to show hierarchy.
        /// </summary>
        private static void AppendGameObjectInspection(System.Text.StringBuilder sb, GameObject go,
            string filterComponent, int maxDepth, int currentDepth, string displayPath)
        {
            string indent = new string(' ', currentDepth * 2);
            sb.AppendLine($"{indent}GameObject: {displayPath ?? go.name}");
            sb.AppendLine($"{indent}active: {go.activeSelf}  layer: {LayerMask.LayerToName(go.layer)}  tag: {go.tag}");

            var components = go.GetComponents<Component>();
            foreach (var comp in components)
            {
                if (comp == null) continue;
                string typeName = comp.GetType().Name;

                if (!string.IsNullOrEmpty(filterComponent) &&
                    !typeName.Equals(filterComponent, StringComparison.OrdinalIgnoreCase))
                    continue;

                sb.AppendLine($"{indent}[{typeName}]");
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

            if (currentDepth >= maxDepth) return;

            foreach (Transform child in go.transform)
            {
                sb.AppendLine();
                AppendGameObjectInspection(sb, child.gameObject, filterComponent,
                    maxDepth, currentDepth + 1, child.name);
            }
        }

        private static string InspectAsset(string assetPath, string filterComponent)
        {
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
                return Response.Error($"Asset not found: {assetPath}");

            // Prefab → inspect like a GameObject (components)
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
                sb.AppendLine($"active: {go.activeSelf}");
                sb.AppendLine($"layer: {LayerMask.LayerToName(go.layer)}");
                sb.AppendLine($"tag: {go.tag}");
                var prefabType = PrefabUtility.GetPrefabAssetType(go);
                sb.AppendLine($"prefabType: {prefabType}");
                if (prefabType == PrefabAssetType.Variant)
                {
                    var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (source != null)
                        sb.AppendLine($"basePrefab: {AssetDatabase.GetAssetPath(source)}");
                }
                sb.AppendLine($"children: {go.transform.childCount}");
                sb.AppendLine("---");

                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;
                    if (!string.IsNullOrEmpty(filterComponent) &&
                        !typeName.Equals(filterComponent, StringComparison.OrdinalIgnoreCase))
                        continue;

                    sb.AppendLine($"[{typeName}]");
                    var so = new SerializedObject(comp);
                    var prop = so.GetIterator();
                    if (prop.NextVisible(true))
                    {
                        do
                        {
                            sb.AppendLine($"  {prop.name}: {GetPropertyValue(prop)}");
                        } while (prop.NextVisible(false));
                    }
                    sb.AppendLine();
                }
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
