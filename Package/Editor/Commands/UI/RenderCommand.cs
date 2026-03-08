using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using Newtonsoft.Json.Linq;

namespace clibridge4unity
{
    public static class RenderCommand
    {
        static readonly string OutputDir = Path.Combine(
            Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(),
            "clibridge4unity_screenshots");

        [BridgeCommand("RENDER",
            "Render prefabs or GameObjects to PNG. Multiple paths = labeled grid.",
            Category = "UI",
            Usage = "RENDER Assets/Prefabs/MyPrefab.prefab\n" +
                    "  RENDER path1.prefab path2.prefab path3.prefab   (grid)\n" +
                    "  RENDER {\"items\":[\"a.prefab\",\"b.prefab\"],\"cols\":3,\"cellWidth\":640,\"cellHeight\":480}\n" +
                    "  RENDER {\"prefab\":\"X.prefab\",\"width\":1920,\"height\":1080}\n" +
                    "  Output: %TEMP%/clibridge4unity_screenshots/render_*.png",
            RequiresMainThread = false)]
        public static async Task<string> Render(string data)
        {
            try
            {
                Directory.CreateDirectory(OutputDir);
                data = data?.Trim() ?? "";

                // ── JSON input ──
                if (data.StartsWith('{') || data.StartsWith('['))
                {
                    if (data.StartsWith('['))
                        data = $"{{\"items\":{data}}}";
                    var json = JObject.Parse(data);

                    // Grid mode: {"items":[...]}
                    if (json["items"] is JArray arr)
                        return await RenderGridAsync(
                            arr.Select(t => t.ToString()).ToList(),
                            json["cols"]?.Value<int>() ?? 0,
                            json["cellWidth"]?.Value<int>() ?? 640,
                            json["cellHeight"]?.Value<int>() ?? 480);

                    // Single-item JSON mode
                    int width = json["width"]?.Value<int>() ?? 1920;
                    int height = json["height"]?.Value<int>() ?? 1080;
                    string path = json["prefab"]?.ToString()
                               ?? json["gameObject"]?.ToString();
                    if (path == null)
                        return Response.Error("Missing prefab or gameObject");
                    return await RenderSingleAsync(path, width, height);
                }

                // ── Plain text: check for multiple paths ──
                var paths = ParseMultiplePaths(data);
                if (paths.Count > 1)
                    return await RenderGridAsync(paths, 0, 640, 480);

                // ── Single path ──
                if (paths.Count == 1)
                    return await RenderSingleAsync(paths[0], 1920, 1080);

                return Response.Error("Usage: RENDER <path> [path2 path3 ...]");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        // ───────────────────── Single item render ─────────────────────

        static async Task<string> RenderSingleAsync(string path, int width, int height)
        {
            if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return await CommandRegistry.RunOnMainThreadAsync(
                    () => RenderPrefab(path, width, height));
            }

            // Auto-detect asset type
            var detected = await CommandRegistry.RunOnMainThreadAsync(() =>
            {
                var assetType = AssetDatabase.GetMainAssetTypeAtPath(path);
                if (assetType == typeof(GameObject)) return "prefab";
                return "gameObject";
            });

            if (detected == "prefab")
                return await CommandRegistry.RunOnMainThreadAsync(() => RenderPrefab(path, width, height));

            return await RenderGameObjectAsync(path, width, height);
        }

        // ───────────────────── Multi-path parsing ─────────────────────

        static List<string> ParseMultiplePaths(string data)
        {
            var paths = new List<string>();
            // Strip any literal quote characters (from shell escaping)
            var remaining = data.Replace("\"", "").Replace("'", "");
            while (!string.IsNullOrEmpty(remaining))
            {
                remaining = remaining.TrimStart();
                if (string.IsNullOrEmpty(remaining)) break;

                // Find the earliest known extension
                int prefabIdx = remaining.IndexOf(".prefab", StringComparison.OrdinalIgnoreCase);
                int unityIdx = remaining.IndexOf(".unity", StringComparison.OrdinalIgnoreCase);

                // Pick whichever extension comes first
                var candidates = new List<(int idx, int len)>();
                if (prefabIdx >= 0) candidates.Add((prefabIdx, ".prefab".Length));
                if (unityIdx >= 0) candidates.Add((unityIdx, ".unity".Length));

                if (candidates.Count == 0)
                {
                    paths.Add(remaining.Trim());
                    break;
                }

                candidates.Sort((a, b) => a.idx.CompareTo(b.idx));
                var (endIdx, extLen) = candidates[0];

                paths.Add(remaining[..(endIdx + extLen)].Trim());
                remaining = remaining[(endIdx + extLen)..];
            }
            return paths;
        }

        // ───────────────────── Grid render ─────────────────────

        static async Task<string> RenderGridAsync(List<string> items, int cols, int cellWidth, int cellHeight)
        {
            int labelHeight = 28;
            if (cols <= 0)
                cols = Math.Min(items.Count, (int)Math.Ceiling(Math.Sqrt(items.Count)));
            int rows = (int)Math.Ceiling((double)items.Count / cols);

            int renderW = cellWidth;
            int renderH = cellHeight - labelHeight;
            var cellImages = new byte[items.Count][];
            var cellLabels = new string[items.Count];

            // Render each item to a temp PNG, read bytes immediately before next render overwrites
            for (int i = 0; i < items.Count; i++)
            {
                string itemPath = items[i].Trim();
                cellLabels[i] = Path.GetFileNameWithoutExtension(itemPath);

                // Render the item at cell size — result contains "output: <path>"
                string renderResult = await RenderSingleAsync(itemPath, renderW, renderH);

                // Extract output path from result
                string tempFile = null;
                foreach (var line in renderResult.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("output:", StringComparison.OrdinalIgnoreCase))
                    {
                        tempFile = trimmed.Substring("output:".Length).Trim();
                        break;
                    }
                }

                if (tempFile != null && File.Exists(tempFile))
                    cellImages[i] = File.ReadAllBytes(tempFile);
            }

            // Compose atlas on main thread
            var result = await CommandRegistry.RunOnMainThreadAsync(() =>
            {
                int atlasW = cellWidth * cols;
                int atlasH = cellHeight * rows;
                var atlas = new Texture2D(atlasW, atlasH, TextureFormat.RGBA32, false);

                var fill = new Color32(25, 25, 25, 255);
                var pixels = new Color32[atlasW * atlasH];
                for (int p = 0; p < pixels.Length; p++) pixels[p] = fill;
                atlas.SetPixels32(pixels);

                var labelBg = new Color32(40, 40, 40, 255);
                var labelFill = new Color32[cellWidth * labelHeight];
                for (int p = 0; p < labelFill.Length; p++) labelFill[p] = labelBg;

                for (int i = 0; i < items.Count; i++)
                {
                    int col = i % cols;
                    int row = i / cols;
                    int x = col * cellWidth;
                    int y = (rows - 1 - row) * cellHeight;

                    // Label bar at top of cell
                    atlas.SetPixels32(x, y + cellHeight - labelHeight, cellWidth, labelHeight, labelFill);

                    if (cellImages[i] != null)
                    {
                        var src = new Texture2D(2, 2);
                        src.LoadImage(cellImages[i]);
                        var rt = RenderTexture.GetTemporary(cellWidth, renderH);
                        Graphics.Blit(src, rt);
                        var prev = RenderTexture.active;
                        RenderTexture.active = rt;
                        var scaled = new Texture2D(cellWidth, renderH, TextureFormat.RGBA32, false);
                        scaled.ReadPixels(new Rect(0, 0, cellWidth, renderH), 0, 0);
                        scaled.Apply();
                        RenderTexture.active = prev;
                        RenderTexture.ReleaseTemporary(rt);

                        atlas.SetPixels(x, y, cellWidth, renderH, scaled.GetPixels());
                        UnityEngine.Object.DestroyImmediate(src);
                        UnityEngine.Object.DestroyImmediate(scaled);
                    }
                }

                atlas.Apply();
                string outputPath = Path.Combine(OutputDir, "render_grid.png");
                File.WriteAllBytes(outputPath, atlas.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(atlas);

                var sb = new StringBuilder();
                sb.AppendLine($"Rendered grid: {items.Count} items ({cols}x{rows})");
                sb.AppendLine($"cell: {cellWidth}x{cellHeight}");
                sb.AppendLine($"atlas: {atlasW}x{atlasH}");
                sb.AppendLine($"output: {outputPath}");
                sb.AppendLine("grid:");
                for (int i = 0; i < items.Count; i++)
                {
                    int c = i % cols;
                    int r = i / cols;
                    sb.AppendLine($"  [{r},{c}] {cellLabels[i]}");
                }
                return sb.ToString().TrimEnd();
            });

            return result;
        }

        // ───────────────────── Prefab Rendering (sync on main thread) ─────────────────────
        static string RenderPrefab(string prefabPath, int width, int height)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                return Response.Error($"Prefab not found: {prefabPath}");

            var canvas = prefab.GetComponentInChildren<Canvas>(true);
            if (canvas != null)
                return RenderUIPrefab(prefab, prefabPath, width, height);

            // UI prefab without a Canvas (e.g. a button, panel, or widget meant to be a child of a Canvas).
            // Detect by checking for RectTransform or common UI components.
            var hasRect = prefab.GetComponent<RectTransform>() != null;
            var hasUI = prefab.GetComponentInChildren<Graphic>(true) != null;
            if (hasRect || hasUI)
                return RenderUIPrefabWithTempCanvas(prefab, prefabPath, width, height);

            return Render3DPrefab(prefab, prefabPath, width, height);
        }

        static string RenderUIPrefab(GameObject prefab, string prefabPath, int width, int height)
        {
            var instance = UnityEngine.Object.Instantiate(prefab);
            instance.hideFlags = HideFlags.HideAndDontSave;

            var canvas = instance.GetComponentInChildren<Canvas>(true);
            canvas.renderMode = RenderMode.ScreenSpaceCamera;

            var camGo = new GameObject("__RENDER_CAM__");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.orthographic = true;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            canvas.worldCamera = cam;

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            cam.targetTexture = rt;

            try
            {
                cam.Render();
                return ReadRtAndSave(rt, width, height, "render_ui.png",
                    $"Rendered UI prefab: {prefabPath}");
            }
            finally
            {
                cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(instance);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        static string RenderUIPrefabWithTempCanvas(GameObject prefab, string prefabPath, int width, int height)
        {
            // Create a temporary Canvas, instantiate the prefab as a child, render, then clean up
            var canvasGo = new GameObject("__RENDER_CANVAS__");
            canvasGo.hideFlags = HideFlags.HideAndDontSave;
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();

            var instance = UnityEngine.Object.Instantiate(prefab, canvasGo.transform);
            instance.hideFlags = HideFlags.HideAndDontSave;

            // Stretch the instance to fill the canvas if it has a RectTransform
            var instanceRect = instance.GetComponent<RectTransform>();
            if (instanceRect != null)
            {
                instanceRect.anchorMin = Vector2.zero;
                instanceRect.anchorMax = Vector2.one;
                instanceRect.offsetMin = Vector2.zero;
                instanceRect.offsetMax = Vector2.zero;
            }

            var camGo = new GameObject("__RENDER_CAM__");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.orthographic = true;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            canvas.worldCamera = cam;

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            cam.targetTexture = rt;

            try
            {
                // Force a layout rebuild so UI elements are positioned correctly
                Canvas.ForceUpdateCanvases();
                cam.Render();
                return ReadRtAndSave(rt, width, height, "render_ui.png",
                    $"Rendered UI prefab (auto-Canvas): {prefabPath}");
            }
            finally
            {
                cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(canvasGo); // destroys instance too (child)
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        static string Render3DPrefab(GameObject prefab, string prefabPath, int width, int height)
        {
            var instance = UnityEngine.Object.Instantiate(prefab);
            instance.hideFlags = HideFlags.HideAndDontSave;

            var renderers = instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                UnityEngine.Object.DestroyImmediate(instance);
                return Response.Error($"No renderers in prefab: {prefabPath}");
            }

            var bounds = renderers[0].bounds;
            foreach (var r in renderers.Skip(1))
                bounds.Encapsulate(r.bounds);

            var camGo = new GameObject("__RENDER_CAM__");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.15f, 0.15f, 0.15f, 1f);
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 1000f;

            var lightGo = new GameObject("__RENDER_LIGHT__");
            lightGo.hideFlags = HideFlags.HideAndDontSave;
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightGo.transform.rotation = Quaternion.Euler(45, -30, 0);

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            cam.targetTexture = rt;

            string[] angleNames = { "front", "front_right", "right", "back_right", "back", "back_left", "left", "front_left" };
            float[] angles = { 0, 45, 90, 135, 180, 225, 270, 315 };
            float dist = bounds.extents.magnitude * 3.5f;
            var center = bounds.center;
            var outputPaths = new StringBuilder();

            try
            {
                for (int i = 0; i < 8; i++)
                {
                    float rad = angles[i] * Mathf.Deg2Rad;
                    camGo.transform.position = center + new Vector3(
                        Mathf.Sin(rad) * dist, dist * 0.4f, Mathf.Cos(rad) * dist);
                    camGo.transform.LookAt(center);

                    cam.Render();

                    string path = Path.Combine(OutputDir, $"render_{angleNames[i]}.png");
                    ReadRtAndSaveToFile(rt, width, height, path);
                    outputPaths.AppendLine($"  {angleNames[i]}: {path}");
                }
            }
            finally
            {
                cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(lightGo);
                UnityEngine.Object.DestroyImmediate(instance);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Rendered 3D prefab: {prefabPath}");
            sb.AppendLine($"size: {width}x{height}");
            sb.AppendLine($"angles: 8");
            sb.Append(outputPaths);
            return sb.ToString().TrimEnd();
        }

        // ───────────────────── Scene GameObject Rendering ─────────────────────
        static async Task<string> RenderGameObjectAsync(string targetPath, int width, int height)
        {
            var result = await CommandRegistry.RunOnMainThreadAsync(() =>
            {
                GameObject target = FindSceneGameObject(targetPath);
                if (target == null)
                    return $"Error: GameObject not found: {targetPath}";

                // Check for Canvas (uGUI)
                var canvas = target.GetComponentInChildren<Canvas>(true)
                          ?? target.GetComponentInParent<Canvas>();
                if (canvas != null)
                    return RenderSceneCanvas(canvas, target, width, height);

                // 3D object
                return RenderScene3D(target, width, height);
            });

            return result;
        }

        static string RenderSceneCanvas(Canvas canvas, GameObject target, int width, int height)
        {
            var origRenderMode = canvas.renderMode;
            var origCamera = canvas.worldCamera;

            canvas.renderMode = RenderMode.ScreenSpaceCamera;

            var camGo = new GameObject("__RENDER_CAM__");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.orthographic = true;
            canvas.worldCamera = cam;

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            cam.targetTexture = rt;

            try
            {
                cam.Render();
                return ReadRtAndSave(rt, width, height, "render_ui.png",
                    $"Rendered scene Canvas: {target.name}");
            }
            finally
            {
                canvas.renderMode = origRenderMode;
                canvas.worldCamera = origCamera;
                cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(camGo);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        static string RenderScene3D(GameObject target, int width, int height)
        {
            var renderers = target.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return Response.Error($"No renderers on: {target.name}");

            var bounds = renderers[0].bounds;
            foreach (var r in renderers.Skip(1))
                bounds.Encapsulate(r.bounds);

            var camGo = new GameObject("__RENDER_CAM__");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.15f, 0.15f, 0.15f, 1f);
            cam.fieldOfView = 30f;

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            cam.targetTexture = rt;

            float dist = bounds.extents.magnitude * 3.5f;
            var center = bounds.center;
            camGo.transform.position = center + new Vector3(0, dist * 0.4f, dist);
            camGo.transform.LookAt(center);

            try
            {
                cam.Render();
                return ReadRtAndSave(rt, width, height, "render_3d.png",
                    $"Rendered 3D object: {target.name}");
            }
            finally
            {
                cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(camGo);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        // ───────────────────── Helpers ─────────────────────
        static GameObject FindSceneGameObject(string path)
        {
            // Check prefab stage first
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null)
            {
                var found = FindInHierarchy(prefabStage.prefabContentsRoot, path);
                if (found != null) return found;
            }

            // Direct find
            var go = GameObject.Find(path);
            if (go != null) return go;

            // Fuzzy search across all root objects
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                var found = FindInHierarchy(root, path);
                if (found != null) return found;
            }
            return null;
        }

        static GameObject FindInHierarchy(GameObject root, string path)
        {
            if (root == null) return null;
            if (root.name == path) return root;

            var found = root.transform.Find(path);
            if (found != null) return found.gameObject;

            string searchName = path.Contains("/") ? path.Substring(path.LastIndexOf('/') + 1) : path;
            return FindChildByName(root.transform, searchName);
        }

        static GameObject FindChildByName(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name) return child.gameObject;
                var found = FindChildByName(child, name);
                if (found != null) return found;
            }
            return null;
        }

        // ───────────────────── Helpers ─────────────────────

        static string ReadRtAndSave(RenderTexture rt, int width, int height, string filename, string header)
        {
            string outputPath = Path.Combine(OutputDir, filename);
            ReadRtAndSaveToFile(rt, width, height, outputPath);

            var sb = new StringBuilder();
            sb.AppendLine(header);
            sb.AppendLine($"size: {width}x{height}");
            sb.AppendLine($"output: {outputPath}");
            return sb.ToString().TrimEnd();
        }

        static void ReadRtAndSaveToFile(RenderTexture rt, int width, int height, string path)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }
}
