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
