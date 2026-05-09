using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
        // Persistent log capture spanning the whole PLAY → STOP window. Subscribed by PLAY,
        // drained + unsubscribed by STOP. Lives outside the per-command capture in LogCommands
        // because PLAY returns BEFORE STOP — a command-scoped buffer would clear between them.
        // Persisted to SessionState so domain reload (entering play mode triggers one) doesn't
        // wipe the in-memory list between PLAY and STOP.
        const string PlayLogStateKey = "Bridge_PlayLogJson";
        const string PlayActiveStateKey = "Bridge_PlayCaptureActive";
        static readonly object PlayLogLock = new object();
        static bool _playLogSubscribed;

        [InitializeOnLoadMethod]
        static void RestoreCaptureSubscription()
        {
            if (SessionState.GetBool(PlayActiveStateKey, false))
            {
                lock (PlayLogLock)
                {
                    if (!_playLogSubscribed)
                    {
                        Application.logMessageReceivedThreaded += OnPlayLog;
                        _playLogSubscribed = true;
                    }
                }
            }
        }

        static void StartPlayLogCapture()
        {
            lock (PlayLogLock)
            {
                SessionState.SetString(PlayLogStateKey, "");
                SessionState.SetBool(PlayActiveStateKey, true);
                try { System.IO.File.WriteAllText(PlayLogFilePath, ""); } catch { }
                if (!_playLogSubscribed)
                {
                    Application.logMessageReceivedThreaded += OnPlayLog;
                    _playLogSubscribed = true;
                }
            }
        }

        static List<(LogType type, string message, string stack, DateTime utc)> ReadPlayLog()
        {
            string raw = SessionState.GetString(PlayLogStateKey, "");
            var list = new List<(LogType, string, string, DateTime)>();
            if (string.IsNullOrEmpty(raw)) return list;
            // One entry per line: <unixSec>|<type>|<message-base64>
            foreach (var line in raw.Split('\n'))
            {
                if (string.IsNullOrEmpty(line)) continue;
                var parts = line.Split(new[] { '|' }, 3);
                if (parts.Length < 3) continue;
                if (!long.TryParse(parts[0], out long ts)) continue;
                if (!Enum.TryParse(parts[1], out LogType type)) type = LogType.Error;
                string msg;
                try { msg = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])); }
                catch { msg = parts[2]; }
                list.Add((type, msg, "", DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime));
            }
            return list;
        }

        static void StopPlayLogCapture(bool unsubscribe, out List<(LogType type, string message, string stack, DateTime utc)> snapshot)
        {
            lock (PlayLogLock)
            {
                if (unsubscribe && _playLogSubscribed)
                {
                    Application.logMessageReceivedThreaded -= OnPlayLog;
                    _playLogSubscribed = false;
                    SessionState.SetBool(PlayActiveStateKey, false);
                }
                snapshot = ReadPlayLog();
                if (unsubscribe) SessionState.SetString(PlayLogStateKey, "");
            }
        }

        // File-based mirror of the SessionState play log so the CLI wrapper can read it directly
        // (without needing to chain a follow-up bridge command after the playmode domain reload).
        // Path: <project>/.clibridge4unity/play_log.txt — cleared at PLAY start, drained at STOP.
        static string PlayLogFilePath
        {
            get
            {
                string projectRoot = Application.dataPath.Replace("/Assets", "");
                string dir = System.IO.Path.Combine(projectRoot, ".clibridge4unity");
                try { System.IO.Directory.CreateDirectory(dir); } catch { }
                return System.IO.Path.Combine(dir, "play_log.txt");
            }
        }

        static void OnPlayLog(string message, string stack, LogType type)
        {
            // Only persist errors/exceptions — Debug.Log spam would balloon SessionState fast.
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            lock (PlayLogLock)
            {
                string current = SessionState.GetString(PlayLogStateKey, "");
                if (current.Length > 200_000) return; // guardrail
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(message ?? ""));
                string line = $"{ts}|{type}|{b64}\n";
                SessionState.SetString(PlayLogStateKey, current + line);
                // Mirror to disk so the CLI can read without round-tripping through Unity (the
                // bridge pipe is dead during the playmode domain-reload window).
                try { System.IO.File.AppendAllText(PlayLogFilePath, $"[{type}] {(message ?? "").Split('\n')[0]}\n"); } catch { }
            }
        }


        static string FormatPlayErrors(List<(LogType type, string message, string stack, DateTime utc)> entries, int maxLines)
        {
            var errs = entries.Where(e => e.type == LogType.Error || e.type == LogType.Exception || e.type == LogType.Assert).ToList();
            if (errs.Count == 0) return null;
            var sb = new StringBuilder();
            sb.AppendLine($"errors: {errs.Count}");
            foreach (var e in errs.Take(maxLines))
            {
                string firstLine = e.message?.Split('\n')[0] ?? "";
                sb.AppendLine($"  [{e.type}] {firstLine}");
            }
            if (errs.Count > maxLines) sb.AppendLine($"  ... +{errs.Count - maxLines} more");
            return sb.ToString().TrimEnd();
        }

        [BridgeCommand("PLAY", "Enter play mode and start capturing errors. STOP returns the captured errors when you exit",
            Category = "Scene",
            Usage = "PLAY  OR  PLAY Assets/Scenes/MyScene.unity",
            RequiresMainThread = true,
            RelatedCommands = new[] { "STOP", "PAUSE", "STEP", "PLAYMODE", "LOG" })]
        public static string Play(string data)
        {
            // Note: a `--wait N` settle was tried but doesn't work — entering play mode triggers
            // a domain reload that kills the bridge pipe mid-await, so the CLI sees an empty
            // response. Capture is started instead; STOP drains it. To peek at errors during a
            // live session, use `LOG errors`.
            try
            {
                string scenePath = string.IsNullOrWhiteSpace(data) ? null : data.Trim();
                if (scenePath != null)
                {
                    if (!System.IO.File.Exists(System.IO.Path.Combine(Application.dataPath, "..", scenePath)))
                        return Response.Error($"Scene not found: {scenePath}");
                    if (EditorApplication.isPlaying)
                        EditorApplication.isPlaying = false;
                    EditorSceneManager.OpenScene(scenePath);
                }

                if (EditorApplication.isPlaying)
                    return Response.Success("Already in play mode");

                SessionState.SetString(SessionKeys.PlayModeStartTime, DateTime.Now.Ticks.ToString());
                StartPlayLogCapture();
                EditorApplication.isPlaying = true;
                return Response.Success("Entered play mode. Capturing errors — STOP returns the session log; `LOG errors` peeks live.");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        [BridgeCommand("STOP", "Exit play mode and return any errors/exceptions that fired during the play session",
            Category = "Scene",
            Usage = "STOP",
            RequiresMainThread = true,
            RelatedCommands = new[] { "PLAY", "LOG" })]
        public static string Stop()
        {
            try
            {
                if (!EditorApplication.isPlaying)
                {
                    // Unsubscribe defensively in case PLAY left a dangling capture (e.g. crash mid-session).
                    StopPlayLogCapture(unsubscribe: true, out _);
                    return Response.Success("Not in play mode");
                }

                EditorApplication.isPlaying = false;
                StopPlayLogCapture(unsubscribe: true, out var captured);
                string errs = FormatPlayErrors(captured, maxLines: 50);
                string note = "If scripts were edited before play mode, Unity auto-compiles after exit " +
                              "cleanup. Do NOT call COMPILE immediately — it triggers a redundant reload " +
                              "on top of the auto-compile, causing cascading domain reloads. " +
                              "Wait for STATUS to show isCompiling=False and isUpdating=False first.";
                if (errs == null)
                    return Response.SuccessWithData(new { message = "Exiting play mode (no errors during session).", note });
                return Response.SuccessWithData(new { message = "Exiting play mode.", playSessionErrors = errs, note });
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

        [BridgeCommand("SCREENSHOT", "Smart screenshot — camera, gameview, GameObjects, prefabs, or UXML",
            Category = "Scene",
            Usage = "SCREENSHOT camera [WxH]                 - Raw camera render, NO overlays (default 960x540)\n" +
                    "  SCREENSHOT gameview                     - GameView tab including OnGUI / UI Toolkit / chrome\n" +
                    "  SCREENSHOT Player                       - Find GameObject, render from multiple angles\n" +
                    "  SCREENSHOT Assets/Prefabs/X.prefab      - Render prefab asset (auto-sized, capped at 1280px)\n" +
                    "  SCREENSHOT Assets/UI/X.uxml             - Render UXML at 800x450\n" +
                    "  SCREENSHOT Assets/UI/X.uxml --el #card  - Render only the matching sub-element\n" +
                    "                                            (--el supports #name, .class, or bare name)",
            RequiresMainThread = false)]
        public static async Task<string> Screenshot(string data)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(data))
                    data = "camera";

                string arg = data.Trim();
                int width = 960, height = 540;

                // Strip --output flag (handled CLI-side, ignore here)
                int outIdx = arg.IndexOf("--output ", System.StringComparison.OrdinalIgnoreCase);
                if (outIdx >= 0)
                    arg = arg.Substring(0, outIdx).Trim();

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

                // Camera-only render (no overlays)
                if (target.Equals("camera", StringComparison.OrdinalIgnoreCase))
                    return await CommandRegistry.RunOnMainThreadAsync(() =>
                        RenderCamera(width, height, outputDir));

                // GameView tab (includes OnGUI, runtime UI Toolkit, chrome)
                // 'game' kept as alias for backwards compatibility — 'gameview' is canonical.
                if (target.Equals("gameview", StringComparison.OrdinalIgnoreCase) ||
                    target.Equals("game", StringComparison.OrdinalIgnoreCase))
                    return await CommandRegistry.RunOnMainThreadAsync(() =>
                        RenderGameView(outputDir));

                // Asset path → inline RenderCommand pipeline (handles its own main thread)
                if (target.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                {
                    return await RenderCommand.Render(target);
                }

                // GameObject by name (needs main thread)
                return await CommandRegistry.RunOnMainThreadAsync(() =>
                    RenderGameObject(target, width, height, outputDir));
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
            // Find the GameObject — try multiple strategies
            var go = GameObject.Find(name); // active objects by path
            if (go == null)
            {
                // Search all root objects (includes inactive) — path match then recursive name match
                string searchName = name.Contains("/") ? name.Substring(name.LastIndexOf('/') + 1) : name;
                foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                {
                    if (root.name == name) { go = root; break; }
                    var found = root.transform.Find(name);
                    if (found != null) { go = found.gameObject; break; }
                    // Recursive name search (finds inactive children too)
                    var byName = FindChildByNameRecursive(root.transform, searchName);
                    if (byName != null) { go = byName; break; }
                }
            }
            if (go == null)
                return Response.ErrorSceneNotFound(name);

            // Heads-up if the GameObject (or an ancestor) is disabled — render will produce blank/empty output
            if (!go.activeInHierarchy)
            {
                string reason = !go.activeSelf
                    ? $"'{name}' is disabled (activeSelf=false)"
                    : $"'{name}' is in a disabled hierarchy (a parent has activeSelf=false)";
                return Response.Error(
                    $"{reason} — enable it in the scene before screenshotting, or it will render as empty.");
            }

            // Detect type: UI (Canvas/RectTransform) vs 3D (Renderer)
            var rectTransform = go.GetComponent<RectTransform>();
            if (rectTransform != null && go.GetComponentInParent<Canvas>() != null)
            {
                // UI element — render the canvas it belongs to
                return Response.Error("UI element rendering: use SCREENSHOT with the asset path");
            }

            // 3D object — get bounds, render from front + right + top (3-view grid)
            var renderer = go.GetComponentInChildren<Renderer>();
            if (renderer == null)
                return Response.Error($"GameObject '{name}' has no Renderer — nothing to capture");
            // All renderers disabled? Bounds will be (0,0,0) and image will be empty.
            var allRenderers = go.GetComponentsInChildren<Renderer>(includeInactive: false);
            if (allRenderers.Length == 0 || allRenderers.All(r => !r.enabled))
                return Response.Error($"GameObject '{name}' has no enabled Renderers — would render empty. Enable a Renderer (or the GameObject) first.");

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
            var rt = new RenderTexture(cellW, cellH, 24, RenderTextureFormat.ARGB32);
            try
            {
                var cam = camGO.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.2f, 0.2f, 0.2f);
                cam.orthographic = true;
                cam.orthographicSize = maxDim * 0.6f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = dist * 3f;

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
                string path = System.IO.Path.Combine(outputDir, $"{name.Replace("/", "_")}.png");
                System.IO.File.WriteAllBytes(path, atlas.EncodeToPNG());
                return Response.Success($"3D render of '{name}' (front|right|top, {cellW}x{cellH} each): {path}");
            }
            finally
            {
                RenderTexture.active = null;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(camGO);
                UnityEngine.Object.DestroyImmediate(atlas);
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        private static string RenderGameView(string outputDir)
        {
            var gameView = GetGameView(create: true);
            if (gameView == null)
                return Response.Error("GameView window not available");

            // Force focus + repaint so GameView shows current frame even if Unity is backgrounded
            gameView.Focus();
            gameView.Repaint();

            var parentField = typeof(EditorWindow).GetField("m_Parent",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var parent = parentField?.GetValue(gameView);
            if (parent == null)
                return Response.Error("GameView has no parent view (window not docked yet)");

            // RepaintImmediately on the host view forces a fresh frame into the backing buffer
            var repaintImm = parent.GetType().GetMethod("RepaintImmediately",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            repaintImm?.Invoke(parent, null);

            var grabMethod = parent.GetType().GetMethod("GrabPixels",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (grabMethod == null)
                return Response.Error("GrabPixels reflection failed (Unity API changed?)");

            var rect = gameView.position;
            float dpi = EditorGUIUtility.pixelsPerPoint;
            int w = Mathf.RoundToInt(rect.width * dpi);
            int h = Mathf.RoundToInt(rect.height * dpi);
            if (w <= 0 || h <= 0)
                return Response.Error($"GameView has invalid size: {w}x{h}");

            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            try
            {
                grabMethod.Invoke(parent, new object[] { rt, new Rect(0, 0, w, h) });

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;

                // GrabPixels writes top-down; Texture2D is bottom-up — flip vertically
                var pixels = tex.GetPixels32();
                var flipped = new Color32[pixels.Length];
                for (int y = 0; y < h; y++)
                    System.Array.Copy(pixels, y * w, flipped, (h - 1 - y) * w, w);
                tex.SetPixels32(flipped);
                tex.Apply();

                string path = System.IO.Path.Combine(outputDir, "gameview.png");
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);

                return Response.Success(
                    $"GameView render ({w}x{h}, includes overlays)\noutput: {path}");
            }
            finally
            {
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        private static EditorWindow GetGameView(bool create = true)
        {
            var assembly = typeof(EditorWindow).Assembly;
            var gameViewType = assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null) return null;

            // Try existing first
            var windows = Resources.FindObjectsOfTypeAll(gameViewType);
            if (windows.Length > 0) return (EditorWindow)windows[0];

            if (create)
                return EditorWindow.GetWindow(gameViewType, false, "Game", false);

            return null;
        }

        private static GameObject FindChildByNameRecursive(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name) return child.gameObject;
                var found = FindChildByNameRecursive(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
