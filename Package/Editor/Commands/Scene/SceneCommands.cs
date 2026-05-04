using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
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
            RequiresMainThread = true,
            RelatedCommands = new[] { "INSPECTOR", "COMPONENT_ADD", "SCREENSHOT" })]
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
        /// Finds GameObjects by name. Default scope is the active scene.
        /// Prefix `prefab:Assets/path.prefab/NameFragment` to search inside a prefab asset.
        /// </summary>
        [BridgeCommand("FIND", "Find GameObject by name — scene (default) or prefab:<assetpath>/<name>",
            Category = "Scene",
            Usage = "FIND MyObject                                  (scene)\n" +
                    "  FIND scene:MyObject                            (explicit scene scope)\n" +
                    "  FIND prefab:Assets/UI/Menu.prefab/Button       (inside a prefab asset — exact or substring match)\n" +
                    "  FIND prefab:Assets/UI/Menu.prefab/Panel,Button (multiple names — comma-separated)",
            RequiresMainThread = true,
            RelatedCommands = new[] { "INSPECTOR", "SCREENSHOT" })]
        public static string Find(string query)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query))
                    return Response.Error("Usage: FIND <name> | FIND prefab:<assetpath>/<name>");

                query = query.Trim();

                // Strip `scene:` prefix — scene is the default.
                if (query.StartsWith("scene:", StringComparison.OrdinalIgnoreCase))
                    query = query.Substring("scene:".Length).TrimStart();

                // Prefab scope: find by name inside a prefab asset (no prior path knowledge required).
                if (query.StartsWith("prefab:", StringComparison.OrdinalIgnoreCase))
                    return FindInPrefab(query.Substring("prefab:".Length).TrimStart());

                // Scene scope: run name search and component type search together.
                var all = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);

                // Exact name match via GameObject.Find (handles paths like "Parent/Child").
                var exactFound = GameObject.Find(query);

                // Case-insensitive substring name match.
                var nameMatches = all
                    .Where(go => go.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Take(20).ToList();

                // Component type match — always tried, not just as fallback.
                var compType = FindComponentType(query);
                List<GameObject> compMatches = compType != null
                    ? UnityEngine.Object.FindObjectsByType(compType, FindObjectsInactive.Include, FindObjectsSortMode.None)
                        .OfType<Component>().Select(c => c.gameObject).Distinct().Take(20).ToList()
                    : new List<GameObject>();

                // Merge: exact > name > component, deduplicated by instance ID.
                var seen = new HashSet<int>();
                var matches = new List<(GameObject go, string matchedBy)>();
                if (exactFound != null && seen.Add(exactFound.GetInstanceID()))
                    matches.Add((exactFound, "name"));
                foreach (var go in nameMatches)
                    if (seen.Add(go.GetInstanceID()))
                        matches.Add((go, "name"));
                foreach (var go in compMatches)
                    if (seen.Add(go.GetInstanceID()))
                        matches.Add((go, compType.Name));

                // Asset search — always run alongside scene search.
                var assetGuids = AssetDatabase.FindAssets(query);
                var assetMatches = assetGuids
                    .Select(g => AssetDatabase.GUIDToAssetPath(g))
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Take(20)
                    .Select(p => new
                    {
                        path = p,
                        name = System.IO.Path.GetFileNameWithoutExtension(p),
                        type = AssetDatabase.GetMainAssetTypeAtPath(p)?.Name ?? "Unknown"
                    })
                    .ToArray();

                if (matches.Count > 0 || assetMatches.Length > 0)
                {
                    if (matches.Count == 1 && assetMatches.Length == 0)
                        Selection.activeGameObject = matches[0].go;
                    return Response.SuccessWithData(new
                    {
                        sceneMatches = matches.Take(20).Select(m => new
                        {
                            name = m.go.name,
                            path = GetPath(m.go),
                            matchedBy = m.matchedBy,
                            components = m.go.GetComponents<Component>()
                                .Where(c => c != null).Select(c => c.GetType().Name).ToArray()
                        }).ToArray(),
                        assetMatches
                    });
                }

                // Nothing found anywhere — fuzzy suggestions.
                var activeScene = SceneManager.GetActiveScene();
                var qLower = query.ToLowerInvariant();
                var suggestions = all
                    .Where(go => go != null && !string.IsNullOrEmpty(go.name))
                    .Select(go => new { go, score = FuzzyScore(qLower, go.name.ToLowerInvariant()) })
                    .Where(s => s.score > 0)
                    .OrderByDescending(s => s.score)
                    .Take(10)
                    .Select(s => new { name = s.go.name, path = GetPath(s.go) })
                    .ToArray();

                return Response.SuccessWithData(new
                {
                    error = $"No GameObjects found matching '{query}'",
                    scene = activeScene.name,
                    scenePath = activeScene.path,
                    totalGameObjects = all.Length,
                    sceneSuggestions = suggestions,
                    hint = suggestions.Length > 0
                        ? "Closest scene names above. Try INSPECTOR <path> or a partial substring."
                        : "Nothing matched. Try INSPECTOR (no path) for whole-scene hierarchy, or ASSET_SEARCH <query>."
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
                    return Response.ErrorSceneNotFound(path);

                Undo.DestroyObjectImmediate(go);
                return Response.Success($"Deleted {path}");
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

        private static readonly string[] SceneViewFlags = { "frame", "2d", "3d" };
        private static readonly string[] SceneViewOptions = { "frame", "padding" };

        [BridgeCommand("SCENEVIEW", "Control the Scene view (2D mode, frame, zoom)",
            Category = "Scene",
            Usage = "SCENEVIEW [mode]\n" +
                    "  SCENEVIEW frame                - Frame current selection\n" +
                    "  SCENEVIEW frame:DialogPanel    - Frame a named object\n" +
                    "  SCENEVIEW 2d                   - Switch to 2D mode\n" +
                    "  SCENEVIEW 3d                   - Switch to 3D mode\n" +
                    "  SCENEVIEW 2d frame:Panel       - Combinable",
            RequiresMainThread = true,
            RelatedCommands = new[] { "SCREENSHOT" })]
        public static string SceneView(string data)
        {
            try
            {
                var sceneView = UnityEditor.SceneView.lastActiveSceneView;
                if (sceneView == null)
                    return Response.Error("No active Scene view");

                // JSON mode preserved for complex cases
                if (data?.TrimStart().StartsWith("{") == true)
                {
                    var json = JObject.Parse(data);
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

                var args = CommandArgs.Parse(data, SceneViewFlags, SceneViewOptions);

                // Apply mode flags
                if (args.Has("2d"))
                {
                    sceneView.in2DMode = true;
                    sceneView.Repaint();
                }
                if (args.Has("3d"))
                {
                    sceneView.in2DMode = false;
                    sceneView.Repaint();
                }

                // Frame: option (frame:ObjectName) or flag (frame selection)
                string frameObj = args.Get("frame");
                float pad = 0.15f;
                if (args.Options.ContainsKey("padding"))
                    float.TryParse(args.Get("padding"), out pad);

                if (!string.IsNullOrEmpty(frameObj))
                    return FrameObject(sceneView, frameObj, pad);

                if (args.Has("frame"))
                {
                    sceneView.FrameSelected();
                    sceneView.Repaint();
                    return Response.Success("Framed selection");
                }

                // No flags at all = frame selection (default)
                if (args.IsEmpty)
                {
                    sceneView.FrameSelected();
                    sceneView.Repaint();
                    return Response.Success("Framed selection");
                }

                return args.WarningPrefix() + Response.Success($"Scene view updated (2D={sceneView.in2DMode})");
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

        /// <summary>
        /// FIND prefab:Assets/UI/Menu.prefab/NameFragment — load prefab asset, walk, list matches.
        /// Supports comma-separated names (OR) in the fragment segment.
        /// </summary>
        private static string FindInPrefab(string scoped)
        {
            int prefabIdx = scoped.IndexOf(".prefab/", StringComparison.OrdinalIgnoreCase);
            if (prefabIdx < 0)
                return Response.Error("Usage: FIND prefab:Assets/path/Foo.prefab/NameFragment");

            string assetPath = scoped.Substring(0, prefabIdx + ".prefab".Length);
            string nameSegment = scoped.Substring(prefabIdx + ".prefab/".Length);
            if (string.IsNullOrWhiteSpace(nameSegment))
                return Response.Error("Name fragment required after prefab path: prefab:<asset>/<name>");

            var names = nameSegment.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
                return Response.ErrorAssetNotFound(assetPath, "Prefab");

            var matches = new List<(string path, GameObject go)>();
            // Collect every node so we can fuzzy-suggest if zero substring hits.
            var allNodes = new List<(string path, string name)>();
            void Walk(Transform t, string path)
            {
                string full = string.IsNullOrEmpty(path) ? t.name : path + "/" + t.name;
                allNodes.Add((full, t.name));
                foreach (var n in names)
                    if (t.name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matches.Add((full, t.gameObject));
                        break;
                    }
                for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), full);
            }
            Walk(prefab.transform, "");

            if (matches.Count == 0)
            {
                // Fuzzy "did you mean" suggestions across all prefab children.
                var suggestions = new List<(int score, string path)>();
                foreach (var n in names)
                {
                    string needleLower = n.ToLowerInvariant();
                    foreach (var node in allNodes)
                    {
                        int s = PathResolver.NameSimilarity(node.name, needleLower);
                        if (s >= 30) suggestions.Add((s, node.path));
                    }
                }
                var top = suggestions.OrderByDescending(x => x.score)
                    .Select(x => x.path).Distinct().Take(5).ToArray();
                var sb = new StringBuilder();
                sb.Append($"Error: No GameObjects in {assetPath} matching [{string.Join(",", names)}]");
                if (top.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Did you mean:");
                    foreach (var p in top) sb.AppendLine($"  {p}");
                }
                return sb.ToString();
            }

            return Response.SuccessWithData(new
            {
                prefab = assetPath,
                filter = names,
                matches = matches.Take(50).Select(m => new
                {
                    path = m.path,
                    components = m.go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).ToArray()
                }).ToArray(),
                truncated = matches.Count > 50
            });
        }

        private static Type FindComponentType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(t => typeof(Component).IsAssignableFrom(t)
                    && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Lightweight similarity score for FIND suggestions. Higher = closer match.
        /// Rewards substring containment in either direction, then character overlap.
        /// Not Levenshtein — cheap and good enough for "did you mean" hints.
        /// </summary>
        private static int FuzzyScore(string query, string candidate)
        {
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(candidate)) return 0;
            if (candidate.Contains(query)) return 1000 + (1000 / candidate.Length);
            if (query.Contains(candidate)) return 800 + (1000 / query.Length);

            int common = 0;
            int qi = 0;
            for (int i = 0; i < candidate.Length && qi < query.Length; i++)
            {
                if (candidate[i] == query[qi]) { common++; qi++; }
            }
            // Require at least half of the query's chars to appear in order.
            if (common * 2 < query.Length) return 0;
            return common * 100 / Math.Max(query.Length, candidate.Length);
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

    }
}
