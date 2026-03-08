using System;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Newtonsoft.Json.Linq;

namespace clibridge4unity
{
    /// <summary>
    /// Scene and GameObject manipulation commands.
    /// </summary>
    public static class SceneCommands
    {
        /// <summary>
        /// Creates a GameObject with optional components.
        /// JSON: {"name":"Name", "parent":"ParentPath", "components":["BoxCollider","Rigidbody"]}
        /// </summary>
        [BridgeCommand("CREATE", "Create a GameObject in the scene",
            Category = "Scene",
            Usage = "CREATE MyObject  OR  CREATE {\"name\":\"MyObject\",\"components\":[\"BoxCollider\"]}",
            RequiresMainThread = true)]
        public static string Create(string jsonData)
        {
            try
            {
                string name;
                string parentPath = null;
                string[] components = Array.Empty<string>();

                // Try to parse as JSON first, fall back to simple string
                if (jsonData.TrimStart().StartsWith("{"))
                {
                    var data = JObject.Parse(jsonData);
                    name = data["name"]?.ToString() ?? "GameObject";
                    parentPath = data["parent"]?.ToString();
                    components = data["components"]?.ToObject<string[]>() ?? Array.Empty<string>();
                }
                else
                {
                    // Simple string format: just the name
                    name = string.IsNullOrWhiteSpace(jsonData) ? "GameObject" : jsonData.Trim();
                }

                var go = new GameObject(name);

                // Set parent
                if (!string.IsNullOrEmpty(parentPath))
                {
                    var parent = GameObject.Find(parentPath);
                    if (parent != null)
                        go.transform.SetParent(parent.transform, false);
                }

                // Add components
                foreach (var comp in components)
                {
                    var type = FindComponentType(comp);
                    if (type != null)
                        go.AddComponent(type);
                }

                Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
                Selection.activeGameObject = go;

                return Response.SuccessWithData(new
                {
                    name = go.name,
                    path = GetPath(go),
                    components = go.GetComponents<Component>().Select(c => c.GetType().Name).ToArray()
                });
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Finds and selects GameObjects by name/path.
        /// </summary>
        [BridgeCommand("FIND", "Find GameObject by name or path",
            Category = "Scene",
            Usage = "FIND MyObject",
            RequiresMainThread = true)]
        public static string Find(string query)
        {
            try
            {
                var found = GameObject.Find(query);
                if (found != null)
                {
                    Selection.activeGameObject = found;
                    return Response.SuccessWithData(new
                    {
                        name = found.name,
                        path = GetPath(found),
                        position = new { x = found.transform.position.x, y = found.transform.position.y, z = found.transform.position.z },
                        components = found.GetComponents<Component>().Select(c => c.GetType().Name).ToArray()
                    });
                }

                // Try pattern search
                var all = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                var matches = all.Where(go => go.name.Contains(query)).Take(10).ToList();

                if (matches.Count == 0)
                    return Response.Error($"No GameObjects found matching '{query}'");

                if (matches.Count == 1)
                {
                    Selection.activeGameObject = matches[0];
                    return Response.SuccessWithData(new
                    {
                        name = matches[0].name,
                        path = GetPath(matches[0])
                    });
                }

                return Response.SuccessWithData(new
                {
                    matches = matches.Select(go => new { name = go.name, path = GetPath(go) }).ToArray()
                });
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Deletes a GameObject by path.
        /// </summary>
        [BridgeCommand("DELETE", "Delete a GameObject",
            Category = "Scene",
            Usage = "DELETE Path/To/Object",
            RequiresMainThread = true)]
        public static string Delete(string path)
        {
            try
            {
                var go = GameObject.Find(path);
                if (go == null)
                    return Response.Error($"GameObject not found: {path}");

                Undo.DestroyObjectImmediate(go);
                return Response.Success($"Deleted {path}");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Gets scene info and hierarchy.
        /// </summary>
        [BridgeCommand("SCENE", "Get current scene info and hierarchy",
            Category = "Scene",
            Usage = "SCENE",
            RequiresMainThread = true)]
        public static string GetInfo()
        {
            try
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                var roots = scene.GetRootGameObjects();
                var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);

                var sb = new StringBuilder();
                void AppendHierarchy(Transform t, int depth)
                {
                    if (depth > 8) return;
                    sb.AppendLine($"{new string(' ', depth * 2)}{t.name}");
                    foreach (Transform child in t)
                        AppendHierarchy(child, depth + 1);
                }

                foreach (var root in roots)
                    AppendHierarchy(root.transform, 0);

                return Response.SuccessWithData(new
                {
                    sceneName = scene.name,
                    scenePath = scene.path,
                    isDirty = scene.isDirty,
                    rootCount = roots.Length,
                    totalObjects = allObjects.Length,
                    hierarchy = sb.ToString()
                });
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Saves the current scene.
        /// </summary>
        [BridgeCommand("SAVE", "Save the current scene",
            Category = "Scene",
            Usage = "SAVE",
            RequiresMainThread = true)]
        public static string Save()
        {
            try
            {
                bool saved = EditorSceneManager.SaveOpenScenes();
                return saved ? Response.Success("Scene saved") : Response.Error("Failed to save scene");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        /// <summary>
        /// Loads a scene.
        /// </summary>
        [BridgeCommand("LOAD", "Load a scene by path",
            Category = "Scene",
            Usage = "LOAD Assets/Scenes/MyScene.unity",
            RequiresMainThread = true)]
        public static string Load(string scenePath)
        {
            try
            {
                if (string.IsNullOrEmpty(scenePath))
                    return Response.Error("Scene path required");

                EditorSceneManager.OpenScene(scenePath);
                return Response.Success($"Loaded {scenePath}");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        [BridgeCommand("SCENEVIEW", "Control the Scene view (2D mode, frame, zoom)",
            Category = "Scene",
            Usage = "SCENEVIEW frame  OR  SCENEVIEW frame:DialogPanel  OR  SCENEVIEW 2d  OR  SCENEVIEW 3d\n" +
                    "  SCENEVIEW {\"frame\":\"Canvas/Panel\",\"2d\":true,\"padding\":0.1}",
            RequiresMainThread = true)]
        public static string SceneView(string data)
        {
            try
            {
                var sceneView = UnityEditor.SceneView.lastActiveSceneView;
                if (sceneView == null)
                    return Response.Error("No active Scene view");

                // Simple string commands
                if (string.IsNullOrWhiteSpace(data) || data.Trim().Equals("frame", StringComparison.OrdinalIgnoreCase))
                {
                    sceneView.FrameSelected();
                    sceneView.Repaint();
                    return Response.Success("Framed selection");
                }

                string trimmed = data.Trim();
                if (trimmed.Equals("2d", StringComparison.OrdinalIgnoreCase))
                {
                    sceneView.in2DMode = true;
                    sceneView.Repaint();
                    return Response.Success("Scene view set to 2D mode");
                }
                if (trimmed.Equals("3d", StringComparison.OrdinalIgnoreCase))
                {
                    sceneView.in2DMode = false;
                    sceneView.Repaint();
                    return Response.Success("Scene view set to 3D mode");
                }

                // frame:ObjectName shorthand
                if (trimmed.StartsWith("frame:", StringComparison.OrdinalIgnoreCase))
                {
                    string target = trimmed["frame:".Length..].Trim();
                    return FrameObject(sceneView, target, 0.15f);
                }

                // JSON mode
                if (trimmed.StartsWith("{"))
                {
                    var json = JObject.Parse(trimmed);
                    string frameTarget = json["frame"]?.ToString();
                    bool? mode2d = json["2d"]?.Value<bool>();
                    float padding = json["padding"]?.Value<float>() ?? 0.15f;

                    if (mode2d.HasValue)
                        sceneView.in2DMode = mode2d.Value;

                    if (!string.IsNullOrEmpty(frameTarget))
                        return FrameObject(sceneView, frameTarget, padding);

                    sceneView.Repaint();
                    return Response.Success($"Scene view updated (2D={sceneView.in2DMode})");
                }

                // Assume it's an object name to frame
                return FrameObject(sceneView, trimmed, 0.15f);
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        private static string FrameObject(UnityEditor.SceneView sceneView, string target, float padding)
        {
            // Try to find the object - check prefab stage first, then scene
            GameObject go = null;

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
            {
                var root = stage.prefabContentsRoot;
                var transforms = root.GetComponentsInChildren<Transform>(true);
                foreach (var t in transforms)
                {
                    if (t.name.Equals(target, StringComparison.OrdinalIgnoreCase))
                    {
                        go = t.gameObject;
                        break;
                    }
                }
                // Also try path match
                if (go == null)
                {
                    foreach (var t in transforms)
                    {
                        string path = GetPath(t.gameObject);
                        if (path.EndsWith(target, StringComparison.OrdinalIgnoreCase))
                        {
                            go = t.gameObject;
                            break;
                        }
                    }
                }
            }

            if (go == null)
                go = GameObject.Find(target);

            if (go == null)
                return Response.Error($"GameObject '{target}' not found");

            // Get bounds from RectTransform (UI) or Renderer
            Bounds bounds;
            var rectTransform = go.GetComponent<RectTransform>();
            if (rectTransform != null)
            {
                var corners = new Vector3[4];
                rectTransform.GetWorldCorners(corners);
                bounds = new Bounds(corners[0], Vector3.zero);
                foreach (var c in corners)
                    bounds.Encapsulate(c);
            }
            else
            {
                var renderers = go.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0)
                {
                    bounds = renderers[0].bounds;
                    for (int i = 1; i < renderers.Length; i++)
                        bounds.Encapsulate(renderers[i].bounds);
                }
                else
                {
                    bounds = new Bounds(go.transform.position, Vector3.one);
                }
            }

            bounds.Expand(bounds.size * padding);
            sceneView.Frame(bounds, false);
            sceneView.Repaint();

            return Response.Success($"Framed '{target}' (size={bounds.size:F1})");
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
    }
}
