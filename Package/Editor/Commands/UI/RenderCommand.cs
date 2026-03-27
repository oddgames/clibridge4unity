using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine.UIElements;
using Newtonsoft.Json.Linq;

namespace clibridge4unity
{
    public static class RenderCommand
    {
        // Win32 APIs for capturing popup window content via PrintWindow (no TOPMOST needed)
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr ho);
        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);
        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint cLines,
            [Out] byte[] lpvBits, ref BITMAPINFO lpbmi, uint usage);
        const uint PW_RENDERFULLCONTENT = 2;
        const uint PW_CLIENTONLY = 1;

        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

        /// <summary>
        /// Captures a window's rendered content via PrintWindow — works even if occluded.
        /// </summary>
        static Texture2D CaptureWindowToTexture(IntPtr hwnd, int width, int height)
        {
            // Use client rect to exclude title bar/borders
            if (GetClientRect(hwnd, out var clientRect))
            {
                int cw = clientRect.right - clientRect.left;
                int ch = clientRect.bottom - clientRect.top;
                if (cw > 0 && ch > 0) { width = cw; height = ch; }
            }

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = CreateCompatibleBitmap(screenDc, width, height);
            IntPtr oldBitmap = SelectObject(memDc, hBitmap);

            PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT | PW_CLIENTONLY);

            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height; // negative = top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;

            byte[] pixels = new byte[width * height * 4];
            GetDIBits(memDc, hBitmap, 0, (uint)height, pixels, ref bmi, 0);

            SelectObject(memDc, oldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);

            // BGRA → RGBA with vertical flip:
            // GetDIBits with biHeight=-height gives top-down rows (row 0 = top of image)
            // Unity Texture2D is bottom-up (index 0 = bottom-left pixel)
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var colors = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                int srcRow = y;
                int dstRow = height - 1 - y; // flip vertically
                for (int x = 0; x < width; x++)
                {
                    int o = (srcRow * width + x) * 4;
                    colors[dstRow * width + x] = new Color32(pixels[o + 2], pixels[o + 1], pixels[o], 255);
                }
            }
            tex.SetPixels32(colors);
            tex.Apply();
            return tex;
        }

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
                return await RenderUxmlAsync(path, width > 0 ? width : 1280, height > 0 ? height : 720);
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

        /// <summary>
        /// Renders a UXML file to PNG using an EditorWindow + GrabPixels.
        /// GrabPixels captures the window's internal backing buffer directly —
        /// no screen capture, no TOPMOST, works even if the window is behind other apps.
        /// </summary>
        static async Task<string> RenderUxmlAsync(string uxmlPath, int width, int height)
        {
            var uxml = await CommandRegistry.RunOnMainThreadAsync(() =>
                AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath));

            if (uxml == null)
            {
                var suggestions = await CommandRegistry.RunOnMainThreadAsync(() => FuzzyAssetSearch(uxmlPath));
                return Response.Error($"UXML not found: {uxmlPath}{suggestions}");
            }

            EditorWindow window = null;

            try
            {
                // Create a utility window hosting the UXML
                await CommandRegistry.RunOnMainThreadAsync<int>(() =>
                {
                    window = ScriptableObject.CreateInstance<EditorWindow>();
                    window.ShowPopup();
                    window.position = new Rect(-4000, -4000, width, height); // offscreen, borderless

                    var root = uxml.Instantiate();
                    root.style.flexGrow = 1;
                    root.style.width = Length.Percent(100);
                    root.style.height = Length.Percent(100);
                    window.rootVisualElement.Add(root);
                    window.Repaint();

                    return 0;
                });

                // Wait for UI Toolkit to layout and paint
                await Task.Delay(500);

                // Use GrabPixels via reflection to capture the internal backing buffer
                return await CommandRegistry.RunOnMainThreadAsync(() =>
                {
                    // Force repaint so content is current
                    var parentField = typeof(EditorWindow).GetField("m_Parent",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var parent = parentField?.GetValue(window);
                    if (parent == null)
                        return Response.Error("EditorWindow has no parent view");

                    // RepaintImmediately ensures the backing buffer is up to date
                    var repaintImm = parent.GetType().GetMethod("RepaintImmediately",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    repaintImm?.Invoke(parent, null);

                    // GrabPixels(RenderTexture, Rect) captures the view's backing buffer
                    var grabMethod = parent.GetType().GetMethod("GrabPixels",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    var rect = window.position;
                    float dpi = EditorGUIUtility.pixelsPerPoint;
                    int w = Mathf.RoundToInt(rect.width * dpi);
                    int h = Mathf.RoundToInt(rect.height * dpi);

                    var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                    rt.Create();
                    try
                    {
                        grabMethod?.Invoke(parent, new object[] { rt, new Rect(0, 0, w, h) });

                        // Read pixels and fix orientation (GrabPixels returns vertically flipped)
                        var prev = RenderTexture.active;
                        RenderTexture.active = rt;
                        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                        tex.Apply();
                        RenderTexture.active = prev;

                        // Flip vertically (GrabPixels is top-down, Texture2D is bottom-up)
                        var pixels = tex.GetPixels32();
                        var flipped = new Color32[pixels.Length];
                        for (int y = 0; y < h; y++)
                        {
                            System.Array.Copy(pixels, y * w, flipped, (h - 1 - y) * w, w);
                        }
                        tex.SetPixels32(flipped);
                        tex.Apply();

                        string outputPath = TimestampedPath(
                            Path.GetFileNameWithoutExtension(uxmlPath));
                        File.WriteAllBytes(outputPath, tex.EncodeToPNG());
                        UnityEngine.Object.DestroyImmediate(tex);

                        return Response.Success($"UXML rendered ({w}x{h})\noutput: {outputPath}");
                    }
                    finally
                    {
                        rt.Release();
                        UnityEngine.Object.DestroyImmediate(rt);
                    }
                });
            }
            finally
            {
                var w = window;
                if (w != null)
                {
                    try { await CommandRegistry.RunOnMainThreadAsync<int>(() => { w.Close(); return 0; }); }
                    catch { }
                }
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
                string outputPath = TimestampedPath("render_grid");
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
                return Response.Error($"Prefab not found: {prefabPath}{FuzzyAssetSearch(prefabPath)}");

            var canvas = prefab.GetComponentInChildren<Canvas>(true);
            if (canvas != null)
            {
                if (width <= 0 || height <= 0)
                {
                    var canvasRect = prefab.GetComponent<RectTransform>();
                    // Check for explicit size (not stretch-anchored)
                    if (canvasRect != null && canvasRect.rect.width > 1 && canvasRect.rect.height > 1
                        && (canvasRect.anchorMin != Vector2.zero || canvasRect.anchorMax != Vector2.one))
                    {
                        width = Mathf.CeilToInt(canvasRect.rect.width);
                        height = Mathf.CeilToInt(canvasRect.rect.height);
                    }
                    else
                    {
                        // Check CanvasScaler reference resolution
                        var scaler = prefab.GetComponent<CanvasScaler>();
                        if (scaler != null && scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize
                            && scaler.referenceResolution.x > 1 && scaler.referenceResolution.y > 1)
                        {
                            width = Mathf.CeilToInt(scaler.referenceResolution.x);
                            height = Mathf.CeilToInt(scaler.referenceResolution.y);
                        }
                        else
                        {
                            width = 1920; height = 1080;
                        }
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

            // Ensure CanvasScaler matches our render resolution
            var scaler = instance.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = instance.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(width, height);
            scaler.matchWidthOrHeight = 0.5f;

            var camGo = new GameObject("__RENDER_CAM__");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 1f);
            cam.orthographic = true;
            cam.orthographicSize = height * 0.5f;
            cam.aspect = (float)width / height;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            canvas.worldCamera = cam;

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            cam.targetTexture = rt;

            try
            {
                Canvas.ForceUpdateCanvases();
                cam.Render();
                return ReadRtAndSave(rt, width, height, "render_ui",
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
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(width, height);
            scaler.matchWidthOrHeight = 0.5f;
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
            }

            var camGo = new GameObject("__RENDER_CAM__");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 1f);
            cam.orthographic = true;
            cam.orthographicSize = height * 0.5f; // match render target so 1 unit = 1 pixel
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            cam.aspect = (float)width / height;
            canvas.worldCamera = cam;

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            cam.targetTexture = rt;

            try
            {
                // Force a layout rebuild so UI elements are positioned correctly
                Canvas.ForceUpdateCanvases();
                cam.Render();
                return ReadRtAndSave(rt, width, height, "render_ui",
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

            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                UnityEngine.Object.DestroyImmediate(instance);
                return Response.Error($"No renderers in prefab: {prefabPath}");
            }

            // Enable disabled renderers temporarily so bounds are accurate
            var wasDisabled = new List<Renderer>();
            foreach (var r in renderers)
            {
                if (!r.enabled) { r.enabled = true; wasDisabled.Add(r); }
            }

            // Compute bounds, filtering outliers (e.g. far-flung collider children)
            var validRenderers = renderers.Where(r => r is MeshRenderer || r is SkinnedMeshRenderer || r is SpriteRenderer).ToArray();
            if (validRenderers.Length == 0) validRenderers = renderers;

            var bounds = validRenderers[0].bounds;
            foreach (var r in validRenderers.Skip(1))
                bounds.Encapsulate(r.bounds);

            var camGo = new GameObject("__RENDER_CAM__");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.15f, 0.15f, 0.15f, 1f);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 1000f;

            // Use FOV that matches the aspect ratio to fully frame the object
            float aspect = (float)width / height;
            float fov = 30f;
            cam.fieldOfView = fov;

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

            // Calculate distance to fully frame the object for the given FOV and aspect ratio
            var center = bounds.center;
            float halfFovRad = fov * 0.5f * Mathf.Deg2Rad;
            float verticalHalfAngle = halfFovRad;
            float horizontalHalfAngle = Mathf.Atan(Mathf.Tan(halfFovRad) * aspect);

            // Distance needed to fit each axis
            float distForHeight = bounds.extents.y / Mathf.Tan(verticalHalfAngle);
            float distForWidth = bounds.extents.x / Mathf.Tan(horizontalHalfAngle);
            float distForDepth = bounds.extents.z / Mathf.Tan(Mathf.Min(verticalHalfAngle, horizontalHalfAngle));

            // Use the largest required distance + padding for the diagonal extent
            float dist = Mathf.Max(distForHeight, Mathf.Max(distForWidth, distForDepth)) * 1.3f;
            // Floor: don't get closer than 2x the largest extent (avoids clipping on thin objects)
            dist = Mathf.Max(dist, bounds.extents.magnitude * 2f);
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

                    string path = TimestampedPath($"render_{angleNames[i]}");
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
                return ReadRtAndSave(rt, width, height, "render_ui",
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
                return ReadRtAndSave(rt, width, height, "render_3d",
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

        // ───────────────────── Fuzzy Asset Search ─────────────────────

        /// <summary>
        /// Searches for similar assets when an exact path isn't found.
        /// Returns a formatted suggestion string with up to 10 matches.
        /// </summary>
        static string FuzzyAssetSearch(string path)
        {
            string filename = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path)?.TrimStart('.');

            // Map extensions to Unity Search type filters
            var typeFilter = ext?.ToLowerInvariant() switch
            {
                "prefab" => "t:Prefab",
                "uxml" => "t:VisualTreeAsset",
                "uss" => "t:StyleSheet",
                "unity" => "t:Scene",
                "mat" => "t:Material",
                "shader" => "t:Shader",
                "png" or "jpg" or "tga" or "psd" => "t:Texture2D",
                "fbx" or "obj" => "t:Model",
                "anim" => "t:AnimationClip",
                "controller" => "t:AnimatorController",
                "asset" => "t:ScriptableObject",
                _ => null
            };

            var results = new List<string>();

            // 1. Fuzzy search by filename
            if (!string.IsNullOrEmpty(filename))
            {
                string query = typeFilter != null ? $"{filename} {typeFilter}" : filename;
                var guids = AssetDatabase.FindAssets(query);
                foreach (var guid in guids)
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    if (p.StartsWith("Assets/") && !results.Contains(p))
                        results.Add(p);
                    if (results.Count >= 10) break;
                }
            }

            // 2. Fallback: search by type/extension only
            if (results.Count == 0 && typeFilter != null)
            {
                var guids = AssetDatabase.FindAssets(typeFilter);
                foreach (var guid in guids)
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    if (p.StartsWith("Assets/") && !results.Contains(p))
                        results.Add(p);
                    if (results.Count >= 10) break;
                }
            }

            // 3. Last resort: search by extension in Assets/
            if (results.Count == 0 && !string.IsNullOrEmpty(ext))
            {
                var guids = AssetDatabase.FindAssets("");
                foreach (var guid in guids)
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    if (p.StartsWith("Assets/") && p.EndsWith($".{ext}", StringComparison.OrdinalIgnoreCase)
                        && !results.Contains(p))
                        results.Add(p);
                    if (results.Count >= 10) break;
                }
            }

            if (results.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine($"\nDid you mean:");
            foreach (var r in results)
                sb.AppendLine($"  {r}");
            return sb.ToString().TrimEnd();
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

        /// <summary>Generates a timestamped filename: name_20260317_083500.png</summary>
        static string TimestampedPath(string baseName)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return Path.Combine(OutputDir, $"{baseName}_{stamp}.png");
        }

        static string ReadRtAndSave(RenderTexture rt, int width, int height, string baseName, string header)
        {
            string outputPath = TimestampedPath(baseName);
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
