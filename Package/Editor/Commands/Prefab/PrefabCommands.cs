using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Newtonsoft.Json.Linq;

namespace clibridge4unity
{
    /// <summary>
    /// Prefab creation and manipulation commands.
    /// </summary>
    public static class PrefabCommands
    {
        /// <summary>
        /// Creates a prefab from scratch or from scene object.
        /// JSON: {"name":"Name", "path":"Assets/Prefabs", "source":"SceneObjectPath", "components":["BoxCollider"]}
        /// </summary>
        [BridgeCommand("PREFAB_CREATE", "Create a prefab asset",
            Category = "Prefab",
            Usage = "PREFAB_CREATE MyPrefab Assets/Prefabs\n" +
                    "  PREFAB_CREATE {\"name\":\"MyPrefab\",\"path\":\"Assets/Prefabs\"}",
            RequiresMainThread = true)]
        public static async Task<string> Create(string jsonData)
        {
            try
            {
                string name;
                string path;
                string source = null;
                string[] components = Array.Empty<string>();

                if (jsonData.TrimStart().StartsWith("{"))
                {
                    var data = JObject.Parse(jsonData);
                    name = data["name"]?.ToString() ?? "NewPrefab";
                    path = data["path"]?.ToString() ?? "Assets/Prefabs";
                    source = data["source"]?.ToString();
                    components = data["components"]?.ToObject<string[]>() ?? Array.Empty<string>();
                }
                else
                {
                    // Plain args: PREFAB_CREATE <name> [path]
                    var parts = ArgParser.Split(jsonData.Trim(), 2);
                    name = parts.Length > 0 ? parts[0] : "NewPrefab";
                    path = parts.Length > 1 ? parts[1] : "Assets/Prefabs";
                }

                GameObject go = null;

                try
                {
                    // Create from existing scene object or new
                    if (!string.IsNullOrEmpty(source))
                    {
                        var sourceGo = GameObject.Find(source);
                        if (sourceGo == null)
                            return Response.Error($"Source object not found: {source}");
                        go = UnityEngine.Object.Instantiate(sourceGo);
                        go.name = name;
                    }
                    else
                    {
                        go = new GameObject(name);
                        foreach (var comp in components)
                        {
                            var type = FindComponentType(comp);
                            if (type != null)
                                go.AddComponent(type);
                        }
                    }

                    // Ensure path ends with .prefab
                    if (!path.EndsWith(".prefab"))
                        path = $"{path}/{name}.prefab";

                    EnsureDirectory(path);
                    var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
                    UnityEngine.Object.DestroyImmediate(go);
                    go = null; // prevent double-destroy in finally

                if (prefab == null)
                    return Response.Error($"Failed to create prefab at {path}");

                    AssetDatabase.Refresh();
                    await Task.Yield();

                    return Response.SuccessWithData(new
                    {
                        path = path,
                        name = prefab.name,
                        components = prefab.GetComponents<Component>().Select(c => c.GetType().Name).ToArray()
                    });
                }
                finally
                {
                    if (go != null) UnityEngine.Object.DestroyImmediate(go);
                }
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Saves a scene GameObject as a prefab asset. Keeps the scene instance connected.
        /// </summary>
        [BridgeCommand("PREFAB_SAVE", "Save a scene GameObject as a prefab asset",
            Category = "Prefab",
            Usage = "PREFAB_SAVE GameObjectName Assets/Prefabs/MyPrefab.prefab\n" +
                    "  PREFAB_SAVE GameObjectName  (auto-saves to Assets/Prefabs/)",
            RequiresMainThread = true)]
        public static string Save(string data)
        {
            try
            {
                if (string.IsNullOrEmpty(data))
                    return Response.Error("Usage: PREFAB_SAVE <gameObjectName> [outputPath]");

                string goName;
                string outputPath;

                if (data.TrimStart().StartsWith("{"))
                {
                    var json = JObject.Parse(data);
                    goName = json["name"]?.ToString() ?? json["gameObject"]?.ToString();
                    outputPath = json["path"]?.ToString();
                }
                else
                {
                    // Plain args: PREFAB_SAVE <name> [path]
                    // Handle paths with spaces — split on .prefab if present
                    int prefabIdx = data.IndexOf(".prefab", StringComparison.OrdinalIgnoreCase);
                    if (prefabIdx >= 0)
                    {
                        // Everything before the last space before .prefab is the GO name
                        // Everything from that space onward is the path
                        int pathStart = data.LastIndexOf(' ', prefabIdx);
                        if (pathStart > 0)
                        {
                            goName = data.Substring(0, pathStart).Trim();
                            outputPath = data.Substring(pathStart).Trim();
                        }
                        else
                        {
                            goName = data.Trim();
                            outputPath = null;
                        }
                    }
                    else
                    {
                        var parts = ArgParser.Split(data.Trim(), 2);
                        goName = parts[0];
                        outputPath = parts.Length > 1 ? parts[1] : null;
                    }
                }

                if (string.IsNullOrEmpty(goName))
                    return Response.Error("GameObject name is required");

                var go = GameObject.Find(goName);
                if (go == null)
                    return Response.Error($"GameObject not found: {goName}");

                // Default path
                if (string.IsNullOrEmpty(outputPath))
                    outputPath = $"Assets/Prefabs/{go.name}.prefab";
                else if (!outputPath.EndsWith(".prefab"))
                    outputPath = $"{outputPath}/{go.name}.prefab";

                EnsureDirectory(outputPath);

                // Save as prefab and keep connected
                var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(
                    go, outputPath, InteractionMode.AutomatedAction);

                if (prefab == null)
                    return Response.Error($"Failed to save prefab at {outputPath}");

                return Response.SuccessWithData(new
                {
                    path = outputPath,
                    name = prefab.name,
                    childCount = prefab.transform.childCount,
                    components = prefab.GetComponents<Component>().Select(c => c.GetType().Name).ToArray()
                });
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Instantiates a prefab in the scene.
        /// JSON: {"prefab":"Assets/Prefabs/Thing.prefab", "position":[x,y,z], "parent":"ParentPath"}
        /// </summary>
        [BridgeCommand("PREFAB_INSTANTIATE", "Instantiate a prefab in the scene",
            Category = "Prefab",
            Usage = "PREFAB_INSTANTIATE Assets/Prefabs/Thing.prefab [parent]\n" +
                    "  PREFAB_INSTANTIATE {\"prefab\":\"Assets/Prefabs/Thing.prefab\",\"parent\":\"Canvas\"}",
            RequiresMainThread = true)]
        public static string Instantiate(string jsonData)
        {
            try
            {
                string prefabPath;
                string parentPath = null;

                if (jsonData.TrimStart().StartsWith("{"))
                {
                    var data = JObject.Parse(jsonData);
                    prefabPath = data["prefab"]?.ToString();
                    parentPath = data["parent"]?.ToString();
                }
                else
                {
                    // Plain args: PREFAB_INSTANTIATE <prefab_path> [parent_path]
                    // Handle paths with spaces by looking for .prefab extension
                    int prefabEnd = jsonData.IndexOf(".prefab", StringComparison.OrdinalIgnoreCase);
                    if (prefabEnd >= 0)
                    {
                        prefabEnd += ".prefab".Length;
                        prefabPath = jsonData.Substring(0, prefabEnd).Trim();
                        if (prefabEnd < jsonData.Length)
                            parentPath = jsonData.Substring(prefabEnd).Trim();
                        if (string.IsNullOrEmpty(parentPath)) parentPath = null;
                    }
                    else
                    {
                        prefabPath = jsonData.Trim();
                    }
                }

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                    return Response.Error($"Prefab not found: {prefabPath}");

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

                // Set position (JSON only)
                if (jsonData.TrimStart().StartsWith("{"))
                {
                    var posData = JObject.Parse(jsonData);
                    if (posData["position"] != null)
                    {
                        var pos = posData["position"].ToObject<float[]>();
                        instance.transform.position = new Vector3(pos[0], pos[1], pos[2]);
                    }
                }

                // Set parent
                if (!string.IsNullOrEmpty(parentPath))
                {
                    var parent = GameObject.Find(parentPath);
                    if (parent != null)
                        instance.transform.SetParent(parent.transform, true);
                }

                Undo.RegisterCreatedObjectUndo(instance, $"Instantiate {prefab.name}");
                Selection.activeGameObject = instance;

                return Response.SuccessWithData(new
                {
                    name = instance.name,
                    path = GetPath(instance),
                    prefabSource = prefabPath
                });
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Gets a prefab's hierarchy with components. Opens prefab, shows hierarchy, then closes it.
        /// Can filter to only show hierarchy paths containing a specific component.
        /// Accepts either:
        ///  - Simple string: "Assets/Prefabs/MyPrefab.prefab"
        ///  - JSON: {"prefab":"Assets/Prefabs/MyPrefab.prefab", "filter":"Dropdown"}
        /// </summary>
        [BridgeCommand("PREFAB_HIERARCHY", "Get prefab hierarchy with components (optionally filtered)",
            Category = "Prefab",
            Usage = "PREFAB_HIERARCHY Assets/Prefabs/MyPrefab.prefab  OR  PREFAB_HIERARCHY {\"prefab\":\"Assets/...\",\"filter\":\"Dropdown\"}",
            RequiresMainThread = true)]
        public static string GetHierarchy(string data)
        {
            try
            {
                string prefabPath;
                string filterComponent = null;

                // Parse input - either simple string or JSON
                if (data.TrimStart().StartsWith("{"))
                {
                    var json = JObject.Parse(data);
                    prefabPath = json["prefab"]?.ToString();
                    filterComponent = json["filter"]?.ToString();
                }
                else
                {
                    prefabPath = data.Trim();
                }

                if (string.IsNullOrEmpty(prefabPath))
                    return Response.Error("Prefab path is required");

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                    return Response.Error($"Prefab not found: {prefabPath}");

                // Open the prefab in edit mode
                var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(prefabPath);
                if (stage == null)
                    return Response.Error($"Failed to open prefab: {prefabPath}");

                try
                {
                    var root = stage.prefabContentsRoot;
                    var sb = new StringBuilder();
                    sb.AppendLine($"Prefab: {prefabPath}");
                    sb.AppendLine($"Root: {root.name}");
                    sb.AppendLine();

                    // Build hierarchy
                    if (string.IsNullOrEmpty(filterComponent))
                    {
                        sb.AppendLine("Full Hierarchy:");
                        BuildHierarchy(root.transform, sb, 0, null);
                    }
                    else
                    {
                        sb.AppendLine($"Objects with {filterComponent}:");
                        var foundObjects = new List<GameObject>();
                        FindObjectsWithComponent(root.transform, filterComponent, foundObjects);

                        if (foundObjects.Count == 0)
                        {
                            sb.AppendLine($"  (none found)");
                        }
                        else
                        {
                            foreach (var obj in foundObjects)
                            {
                                sb.AppendLine();
                                sb.AppendLine($"  Path: {GetPrefabPath(obj.transform, root.transform)}");
                                sb.AppendLine($"  Components:");
                                foreach (var comp in obj.GetComponents<Component>())
                                {
                                    if (comp != null)
                                        sb.AppendLine($"    - {comp.GetType().Name}");
                                }
                            }
                        }
                    }

                    return sb.ToString().TrimEnd();
                }
                finally
                {
                    // Always close the prefab stage, even on exception
                    UnityEditor.SceneManagement.StageUtility.GoToMainStage();
                }
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Applies changes to prefab from scene instance.
        /// </summary>
        public static string Apply(string instancePath)
        {
            try
            {
                var instance = GameObject.Find(instancePath);
                if (instance == null)
                    return Response.Error($"Instance not found: {instancePath}");

                var prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(instance);
                if (prefabRoot == null)
                    return Response.Error($"Object is not a prefab instance: {instancePath}");

                var prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance);
                PrefabUtility.ApplyPrefabInstance(prefabRoot, InteractionMode.UserAction);

                return Response.Success($"Applied changes to {prefabPath}");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Unpacks a prefab instance.
        /// </summary>
        public static string Unpack(string instancePath, bool completely = false)
        {
            try
            {
                var instance = GameObject.Find(instancePath);
                if (instance == null)
                    return Response.Error($"Instance not found: {instancePath}");

                var mode = completely ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot;
                PrefabUtility.UnpackPrefabInstance(instance, mode, InteractionMode.UserAction);

                return Response.Success($"Unpacked {instancePath}");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Lists prefabs in a folder.
        /// </summary>
        public static string List(string folder = "Assets")
        {
            try
            {
                var guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
                var prefabs = guids.Select(g => AssetDatabase.GUIDToAssetPath(g)).ToArray();

                return Response.SuccessWithData(new
                {
                    count = prefabs.Length,
                    prefabs = prefabs.Take(100).ToArray()
                });
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        private static void EnsureDirectory(string filePath)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                AssetDatabase.Refresh();
            }
        }

        private static string GetPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        private static Type FindComponentType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetTypes()
                        .FirstOrDefault(t => typeof(Component).IsAssignableFrom(t) &&
                            t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        private static void BuildHierarchy(Transform t, StringBuilder sb, int depth, string filterComponent)
        {
            if (depth > 20) return; // Prevent infinite recursion

            var indent = new string(' ', depth * 2);
            sb.Append($"{indent}{t.name}");

            // Show components
            var components = t.GetComponents<Component>()
                .Where(c => c != null && c.GetType() != typeof(Transform))
                .Select(c => c.GetType().Name)
                .ToArray();

            if (components.Length > 0)
            {
                sb.Append($" [{string.Join(", ", components)}]");
            }

            sb.AppendLine();

            // Recurse to children
            foreach (Transform child in t)
            {
                BuildHierarchy(child, sb, depth + 1, filterComponent);
            }
        }

        private static void FindObjectsWithComponent(Transform t, string componentName, List<GameObject> results)
        {
            // Check if this GameObject has the component
            var hasComponent = t.GetComponents<Component>()
                .Any(c => c != null && c.GetType().Name.Equals(componentName, StringComparison.OrdinalIgnoreCase));

            if (hasComponent)
            {
                results.Add(t.gameObject);
            }

            // Recurse to children
            foreach (Transform child in t)
            {
                FindObjectsWithComponent(child, componentName, results);
            }
        }

        private static string GetPrefabPath(Transform t, Transform root)
        {
            if (t == root)
                return root.name;

            var path = t.name;
            var parent = t.parent;

            while (parent != null && parent != root)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return root.name + "/" + path;
        }
    }
}
