using System;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace clibridge4unity
{
    /// <summary>
    /// Play mode control and scene loading commands.
    /// </summary>
    public static class PlayModeCommands
    {
        [BridgeCommand("PLAY", "Enter play mode (optionally load a scene first)",
            Category = "Scene",
            Usage = "PLAY  OR  PLAY Assets/Scenes/MyScene.unity",
            RequiresMainThread = true)]
        public static string Play(string data)
        {
            try
            {
                // Optionally load a scene before playing
                if (!string.IsNullOrWhiteSpace(data))
                {
                    string scenePath = data.Trim();
                    if (!System.IO.File.Exists(System.IO.Path.Combine(
                        Application.dataPath, "..", scenePath)))
                    {
                        return Response.Error($"Scene not found: {scenePath}");
                    }

                    if (EditorApplication.isPlaying)
                        EditorApplication.isPlaying = false;

                    EditorSceneManager.OpenScene(scenePath);
                }

                if (EditorApplication.isPlaying)
                    return Response.Success("Already in play mode");

                EditorApplication.isPlaying = true;
                return Response.Success("Entering play mode");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        [BridgeCommand("STOP", "Exit play mode",
            Category = "Scene",
            Usage = "STOP",
            RequiresMainThread = true)]
        public static string Stop()
        {
            try
            {
                if (!EditorApplication.isPlaying)
                    return Response.Success("Not in play mode");

                EditorApplication.isPlaying = false;
                return Response.Success("Exiting play mode");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        [BridgeCommand("PAUSE", "Toggle pause in play mode",
            Category = "Scene",
            Usage = "PAUSE",
            RequiresMainThread = true)]
        public static string Pause()
        {
            try
            {
                if (!EditorApplication.isPlaying)
                    return Response.Error("Not in play mode");

                EditorApplication.isPaused = !EditorApplication.isPaused;
                return Response.Success(EditorApplication.isPaused ? "Paused" : "Unpaused");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        [BridgeCommand("STEP", "Execute a single frame step while paused",
            Category = "Scene",
            Usage = "STEP",
            RequiresMainThread = true)]
        public static string Step()
        {
            try
            {
                if (!EditorApplication.isPlaying)
                    return Response.Error("Not in play mode");

                EditorApplication.Step();
                return Response.Success("Stepped one frame");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        [BridgeCommand("PLAYMODE", "Get current play mode state",
            Category = "Scene",
            Usage = "PLAYMODE",
            RequiresMainThread = true)]
        public static string PlayMode()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"isPlaying: {EditorApplication.isPlaying}");
                sb.AppendLine($"isPaused: {EditorApplication.isPaused}");
                sb.AppendLine($"isCompiling: {EditorApplication.isCompiling}");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        [BridgeCommand("GAMEVIEW", "Set Game view resolution/size",
            Category = "Scene",
            Usage = "GAMEVIEW 1280x720",
            RequiresMainThread = true)]
        public static string GameView(string data)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(data))
                {
                    // Report current game view info
                    var gameView = GetGameView();
                    if (gameView == null)
                        return Response.Error("Game view not found");

                    var pos = gameView.position;
                    return Response.Success($"position: {(int)pos.x},{(int)pos.y}\nsize: {(int)pos.width}x{(int)pos.height}");
                }

                // Parse resolution: "1280x720" or "1280 720"
                var parts = data.Trim().Split(new[] { 'x', 'X', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height))
                    return Response.Error("Invalid format. Use: GAMEVIEW 1280x720");

                var gv = GetGameView();
                if (gv == null)
                    return Response.Error("Game view not found");

                // Resize the game view window
                var rect = gv.position;
                gv.position = new Rect(rect.x, rect.y, width, height);
                gv.Repaint();

                return Response.Success($"Game view resized to {width}x{height}");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        [BridgeCommand("SCREENSHOT", "Smart screenshot — captures camera, GameObjects, prefabs, or UXML",
            Category = "Scene",
            Usage = "SCREENSHOT camera [WxH]       - Render main camera (default 1280x720)\n" +
                    "  SCREENSHOT Player              - Find GameObject, render from multiple angles\n" +
                    "  SCREENSHOT Assets/Prefabs/X.prefab - Render prefab asset\n" +
                    "  SCREENSHOT Assets/UI/X.uxml    - Render UXML at 1280x720",
            RequiresMainThread = true)]
        public static string Screenshot(string data)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(data))
                    data = "camera";

                string arg = data.Trim();
                int width = 1280, height = 720;

                // Parse trailing resolution: "camera 1920x1080" or "Player 800x600"
                string target = arg;
                int lastSpace = arg.LastIndexOf(' ');
                if (lastSpace > 0)
                {
                    string maybeDims = arg.Substring(lastSpace + 1);
                    var dimParts = maybeDims.Split(new[] { 'x', 'X' });
                    if (dimParts.Length == 2 && int.TryParse(dimParts[0], out int w) && int.TryParse(dimParts[1], out int h))
                    {
                        width = w; height = h;
                        target = arg.Substring(0, lastSpace).Trim();
                    }
                }

                string outputDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clibridge4unity_screenshots");
                System.IO.Directory.CreateDirectory(outputDir);

                // Camera render
                if (target.Equals("camera", StringComparison.OrdinalIgnoreCase))
                    return RenderCamera(width, height, outputDir);

                // Asset path: delegate to UI_RENDER command
                if (target.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                {
                    return CommandRegistry.ExecuteCommand("UI_RENDER", target, null, default).Result;
                }

                // GameObject by name — find it, render from multiple angles
                return RenderGameObject(target, width, height, outputDir);
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        private static string RenderCamera(int width, int height, string outputDir)
        {
            var cam = Camera.main ?? UnityEngine.Object.FindFirstObjectByType<Camera>();
            if (cam == null)
                return Response.Error("No camera found in the scene");

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            var prevTarget = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prevTarget;

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            rt.Release();

            string path = System.IO.Path.Combine(outputDir, "camera.png");
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(rt);

            return Response.Success($"Camera render ({width}x{height}): {path}");
        }

        private static string RenderGameObject(string name, int width, int height, string outputDir)
        {
            // Find the GameObject
            var go = GameObject.Find(name);
            if (go == null)
            {
                // Try fuzzy: search all root objects
                foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                {
                    var found = root.transform.Find(name);
                    if (found != null) { go = found.gameObject; break; }
                }
            }
            if (go == null)
                return Response.Error($"GameObject not found: {name}");

            // Detect type: UI (Canvas/RectTransform) vs 3D (Renderer)
            var rectTransform = go.GetComponent<RectTransform>();
            if (rectTransform != null && go.GetComponentInParent<Canvas>() != null)
            {
                // UI element — render the canvas it belongs to
                return Response.Error("UI element rendering: use UI_RENDER on the prefab/UXML asset");
            }

            // 3D object — get bounds, render from front + right + top (3-view grid)
            var renderer = go.GetComponentInChildren<Renderer>();
            if (renderer == null)
                return Response.Error($"GameObject '{name}' has no Renderer — nothing to capture");

            var bounds = renderer.bounds;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
                bounds.Encapsulate(r.bounds);

            float maxDim = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            float dist = maxDim * 2f;
            var center = bounds.center;

            // 3 views: front, right, top
            var angles = new[]
            {
                ("front", center + Vector3.forward * dist, Quaternion.LookRotation(-Vector3.forward)),
                ("right", center + Vector3.right * dist, Quaternion.LookRotation(-Vector3.right)),
                ("top",   center + Vector3.up * dist,     Quaternion.LookRotation(-Vector3.up, Vector3.forward))
            };

            int cellW = width, cellH = height;
            var atlas = new Texture2D(cellW * 3, cellH, TextureFormat.RGB24, false);

            var camGO = new GameObject("__screenshot_cam__");
            var cam = camGO.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.2f, 0.2f, 0.2f);
            cam.orthographic = true;
            cam.orthographicSize = maxDim * 0.6f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = dist * 3f;

            var rt = new RenderTexture(cellW, cellH, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            cam.targetTexture = rt;

            for (int i = 0; i < angles.Length; i++)
            {
                var (label, pos, rot) = angles[i];
                camGO.transform.position = pos;
                camGO.transform.rotation = rot;
                cam.Render();

                RenderTexture.active = rt;
                var cell = new Texture2D(cellW, cellH, TextureFormat.RGB24, false);
                cell.ReadPixels(new Rect(0, 0, cellW, cellH), 0, 0);
                cell.Apply();
                RenderTexture.active = null;

                atlas.SetPixels(i * cellW, 0, cellW, cellH, cell.GetPixels());
                UnityEngine.Object.DestroyImmediate(cell);
            }

            atlas.Apply();
            rt.Release();
            UnityEngine.Object.DestroyImmediate(camGO);

            string path = System.IO.Path.Combine(outputDir, $"{name.Replace("/", "_")}.png");
            System.IO.File.WriteAllBytes(path, atlas.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(atlas);
            UnityEngine.Object.DestroyImmediate(rt);

            return Response.Success($"3D render of '{name}' (front|right|top, {cellW}x{cellH} each): {path}");
        }

        private static EditorWindow GetGameView()
        {
            var assembly = typeof(EditorWindow).Assembly;
            var gameViewType = assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null) return null;

            var windows = Resources.FindObjectsOfTypeAll(gameViewType);
            return windows.Length > 0 ? (EditorWindow)windows[0] : null;
        }
    }
}
