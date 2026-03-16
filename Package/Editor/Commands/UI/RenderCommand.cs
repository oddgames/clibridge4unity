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

        [BridgeCommand("SCREENSHOT_ASSET",
            "Render prefabs, UXML, or GameObjects to PNG (called internally by SCREENSHOT)",
            Category = "Scene",
            Usage = "SCREENSHOT_ASSET Assets/Prefabs/MyPrefab.prefab\n" +
                    "  SCREENSHOT_ASSET path1.prefab path2.prefab   (grid)\n" +
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

                    // Single-item JSON mode (0 = auto-size from content)
                    int width = json["width"]?.Value<int>() ?? 0;
                    int height = json["height"]?.Value<int>() ?? 0;
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

                // ── Single path ── (0,0 = auto-size from content bounds)
                if (paths.Count == 1)
                    return await RenderSingleAsync(paths[0], 0, 0);

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

            if (path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
            {
                return await CommandRegistry.RunOnMainThreadAsync(
                    () => RenderUxml(path, width > 0 ? width : 1280, height > 0 ? height : 720));
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

        static string RenderUxml(string uxmlPath, int width, int height)
        {
            var uxml = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>(uxmlPath);
            if (uxml == null)
                return Response.Error($"UXML not found: {uxmlPath}");

            // Create a temporary UIDocument to render the UXML
            var go = new GameObject("__uxml_render__");
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            try
            {
                rt.Create();

                // Create a runtime panel and render to texture
                var doc = go.AddComponent<UnityEngine.UIElements.UIDocument>();

                // Find or create PanelSettings
                var panelGuids = AssetDatabase.FindAssets("t:PanelSettings");
                if (panelGuids.Length > 0)
                {
                    doc.panelSettings = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>(
                        AssetDatabase.GUIDToAssetPath(panelGuids[0]));
                }
                else
                {
                    // Create temp PanelSettings
                    var ps = ScriptableObject.CreateInstance<UnityEngine.UIElements.PanelSettings>();
                    ps.targetTexture = rt;
                    ps.scaleMode = UnityEngine.UIElements.PanelScaleMode.ConstantPixelSize;
                    doc.panelSettings = ps;
                }

                doc.visualTreeAsset = uxml;

                // Also load any USS referenced in the same directory
                string dir = Path.GetDirectoryName(uxmlPath);
                string ussPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(uxmlPath) + ".uss");
                if (File.Exists(Path.GetFullPath(ussPath)))
                {
                    var uss = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.StyleSheet>(ussPath);
                    if (uss != null && doc.rootVisualElement != null)
                        doc.rootVisualElement.styleSheets.Add(uss);
                }

                // Force a layout pass
                if (doc.rootVisualElement != null)
                {
                    doc.rootVisualElement.style.width = width;
                    doc.rootVisualElement.style.height = height;
                }

                // Render via a camera pointed at nothing (just to capture the UI overlay)
                var camGo = new GameObject("__uxml_cam__");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.12f, 0.12f, 0.14f);
                cam.cullingMask = 0; // render nothing except UI
                cam.targetTexture = rt;
                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;

                string outputPath = Path.Combine(OutputDir, Path.GetFileNameWithoutExtension(uxmlPath) + ".png");
                File.WriteAllBytes(outputPath, tex.EncodeToPNG());

                UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(camGo);

                return Response.Success($"UXML rendered ({width}x{height})\noutput: {outputPath}");
            }
            finally
            {
                RenderTexture.active = null;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(rt);
            }
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

        /// <summary>
        /// Measures a UI prefab's natural size by instantiating under a temp Canvas, forcing layout,
        /// then computing the enclosing bounds of all Graphic components. Adds padding.
        /// Returns (width, height) in pixels. Falls back to 1920x1080 if measurement fails.
        /// </summary>
        static (int w, int h) MeasureUIPrefabSize(GameObject prefab)
        {
            var canvasGo = new GameObject("__MEASURE_CANVAS__");
            canvasGo.hideFlags = HideFlags.HideAndDontSave;
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            var instance = UnityEngine.Object.Instantiate(prefab, canvasGo.transform);
            instance.hideFlags = HideFlags.HideAndDontSave;

            // Center the instance, keep its natural size
            var instanceRect = instance.GetComponent<RectTransform>();
            if (instanceRect != null)
            {
                instanceRect.anchorMin = new Vector2(0.5f, 0.5f);
                instanceRect.anchorMax = new Vector2(0.5f, 0.5f);
                instanceRect.anchoredPosition = Vector2.zero;
            }

            Canvas.ForceUpdateCanvases();

            // Measure bounds from all RectTransforms with Graphic components
            int w = 1920, h = 1080;
            var graphics = instance.GetComponentsInChildren<Graphic>(true);
            if (graphics.Length > 0)
            {
                // Also include the root RectTransform
                var allRects = instance.GetComponentsInChildren<RectTransform>(true);
                if (allRects.Length > 0)
                {
                    float minX = float.MaxValue, minY = float.MaxValue;
                    float maxX = float.MinValue, maxY = float.MinValue;
                    foreach (var rt in allRects)
                    {
                        Vector3[] corners = new Vector3[4];
                        rt.GetWorldCorners(corners);
                        foreach (var c in corners)
                        {
                            if (c.x < minX) minX = c.x;
                            if (c.y < minY) minY = c.y;
                            if (c.x > maxX) maxX = c.x;
                            if (c.y > maxY) maxY = c.y;
                        }
                    }
                    int measuredW = Mathf.CeilToInt(maxX - minX);
                    int measuredH = Mathf.CeilToInt(maxY - minY);
                    if (measuredW > 4 && measuredH > 4)
                    {
                        // Add 10% padding, minimum 20px each side
                        int padX = Mathf.Max(20, Mathf.CeilToInt(measuredW * 0.05f));
                        int padY = Mathf.Max(20, Mathf.CeilToInt(measuredH * 0.05f));
                        w = measuredW + padX * 2;
                        h = measuredH + padY * 2;
                    }
                }
            }
            else if (instanceRect != null)
            {
                // No graphics but has RectTransform — use sizeDelta
                var size = instanceRect.rect.size;
                if (size.x > 4 && size.y > 4)
                {
                    w = Mathf.CeilToInt(size.x) + 40;
                    h = Mathf.CeilToInt(size.y) + 40;
                }
            }

            UnityEngine.Object.DestroyImmediate(canvasGo);

            // If the measured size is very small, the prefab likely auto-sizes (e.g. TMP text)
            // or uses layout groups. Use a sensible default that shows content clearly.
            if (w < 200 || h < 200)
            {
                // Scale up proportionally, minimum 400px on the short side
                float scale = 400f / Mathf.Min(w, h);
                w = Mathf.CeilToInt(w * scale);
                h = Mathf.CeilToInt(h * scale);
            }

            // Clamp to reasonable bounds
            w = Mathf.Clamp(w, 200, 3840);
            h = Mathf.Clamp(h, 200, 2160);
            return (w, h);
        }

        static string RenderPrefab(string prefabPath, int width, int height)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                return Response.Error($"Prefab not found: {prefabPath}");

            var canvas = prefab.GetComponentInChildren<Canvas>(true);
            if (canvas != null)
            {
                // Auto-size: use the Canvas's RectTransform size
                if (width <= 0 || height <= 0)
                {
                    var canvasRect = prefab.GetComponent<RectTransform>();
                    if (canvasRect != null && canvasRect.rect.width > 1 && canvasRect.rect.height > 1)
                    {
                        width = Mathf.CeilToInt(canvasRect.rect.width);
                        height = Mathf.CeilToInt(canvasRect.rect.height);
                    }
                    else
                    {
                        width = 1920; height = 1080;
                    }
                }
                return RenderUIPrefab(prefab, prefabPath, width, height);
            }

            // UI prefab without a Canvas (e.g. a button, panel, or widget meant to be a child of a Canvas).
            var hasRect = prefab.GetComponent<RectTransform>() != null;
            var hasUI = prefab.GetComponentInChildren<Graphic>(true) != null;
            if (hasRect || hasUI)
            {
                // Auto-size: measure the prefab's natural dimensions
                if (width <= 0 || height <= 0)
                {
                    (width, height) = MeasureUIPrefabSize(prefab);
                }
                return RenderUIPrefabWithTempCanvas(prefab, prefabPath, width, height);
            }

            if (width <= 0 || height <= 0) { width = 1024; height = 1024; }
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
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 1f);
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

            // Center the instance in the canvas at its natural size (don't stretch)
            var instanceRect = instance.GetComponent<RectTransform>();
            if (instanceRect != null)
            {
                instanceRect.anchorMin = new Vector2(0.5f, 0.5f);
                instanceRect.anchorMax = new Vector2(0.5f, 0.5f);
                instanceRect.anchoredPosition = Vector2.zero;
                // Keep the prefab's original sizeDelta (natural size)
            }

            var camGo = new GameObject("__RENDER_CAM__");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 1f);
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
            // Auto-size: resolve dimensions on main thread first
            if (width <= 0 || height <= 0)
            {
                var size = await CommandRegistry.RunOnMainThreadAsync<(int w, int h)>(() =>
                {
                    var target = FindSceneGameObject(targetPath);
                    if (target == null) return (1920, 1080);
                    var canvas = target.GetComponentInChildren<Canvas>(true)
                              ?? target.GetComponentInParent<Canvas>();
                    if (canvas != null)
                    {
                        var canvasRect = canvas.GetComponent<RectTransform>();
                        if (canvasRect != null && canvasRect.rect.width > 1 && canvasRect.rect.height > 1)
                            return (Mathf.CeilToInt(canvasRect.rect.width), Mathf.CeilToInt(canvasRect.rect.height));
                        return (1920, 1080);
                    }
                    return (1024, 1024);
                });
                width = size.w;
                height = size.h;
            }

            int w = width, h = height;
            var result = await CommandRegistry.RunOnMainThreadAsync(() =>
            {
                GameObject target = FindSceneGameObject(targetPath);
                if (target == null)
                    return $"Error: GameObject not found: {targetPath}";

                var canvas = target.GetComponentInChildren<Canvas>(true)
                          ?? target.GetComponentInParent<Canvas>();
                if (canvas != null)
                    return RenderSceneCanvas(canvas, target, w, h);

                return RenderScene3D(target, w, h);
            });

            return result;
        }

        static string RenderSceneCanvas(Canvas canvas, GameObject target, int width, int height)
        {
            var origRenderMode = canvas.renderMode;
            var origCamera = canvas.worldCamera;

            // Temporarily activate the canvas hierarchy if inactive
            var inactiveObjects = new List<GameObject>();
            for (var t = canvas.transform; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf)
                {
                    inactiveObjects.Add(t.gameObject);
                    t.gameObject.SetActive(true);
                }
            }

            // Switch to WorldSpace — ScreenSpaceOverlay bypasses cameras entirely
            canvas.renderMode = RenderMode.WorldSpace;
            var canvasRect = canvas.GetComponent<RectTransform>();

            // Save original transform
            var origPos = canvasRect.position;
            var origRot = canvasRect.rotation;
            var origScale = canvasRect.localScale;

            // Set pivot to center, position at origin
            canvasRect.pivot = new Vector2(0.5f, 0.5f);
            canvasRect.position = Vector3.zero;
            canvasRect.rotation = Quaternion.identity;
            canvasRect.localScale = Vector3.one;

            // Force layout rebuild in new mode
            Canvas.ForceUpdateCanvases();

            // Measure actual content bounds after layout
            float canvasW = canvasRect.rect.width;
            float canvasH = canvasRect.rect.height;

            // Camera: orthographic, centered on canvas, sized to fit
            var camGo = new GameObject("__RENDER_CAM__");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 1f);
            cam.orthographic = true;
            cam.cullingMask = -1;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;

            // Match camera aspect to render target, fit canvas height
            float renderAspect = (float)width / height;
            float canvasAspect = canvasW / canvasH;
            if (canvasAspect > renderAspect)
                cam.orthographicSize = (canvasW / renderAspect) * 0.5f;
            else
                cam.orthographicSize = canvasH * 0.5f;

            camGo.transform.position = new Vector3(0, 0, -10f);
            camGo.transform.rotation = Quaternion.identity;

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
                canvasRect.position = origPos;
                canvasRect.rotation = origRot;
                canvasRect.localScale = origScale;
                canvas.renderMode = origRenderMode;
                canvas.worldCamera = origCamera;
                foreach (var go in inactiveObjects)
                    go.SetActive(false);
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
