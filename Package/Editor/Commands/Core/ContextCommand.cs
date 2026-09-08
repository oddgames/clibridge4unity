using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace clibridge4unity
{
    /// <summary>
    /// One round trip that snapshots what the editor is showing right now — scene, play mode,
    /// selection with its serialized fields, console errors — as markdown ready to paste.
    ///
    /// This exists for the capture daemon. A screenshot is taken from outside Unity, so the state
    /// that explains it can only be fetched over the pipe, and it has to be fetched AT capture time:
    /// ask thirty seconds later and the selection has moved, play mode has exited, and the console
    /// has scrolled. One command rather than three (SELECTION + INSPECTOR + LOG) because each pipe
    /// round trip waits on the main thread, and the main thread is exactly what a busy editor
    /// doesn't have spare.
    /// </summary>
    public static class ContextCommand
    {
        [BridgeCommand("CONTEXT", "Snapshot current editor/game state as markdown (scene, play mode, selection + fields, console errors)",
            Category = "Core",
            Usage = "CONTEXT                     (scene + play mode + selection with fields + console errors)\n" +
                    "  CONTEXT --hierarchy       (also include the full scene hierarchy, brief)\n" +
                    "  CONTEXT --refs            (selection dumped as a reference-wiring audit instead of fields)\n" +
                    "  CONTEXT --brief           (selection components only, no serialized fields)\n" +
                    "  CONTEXT --no-console      (skip console errors)\n" +
                    "  CONTEXT --rect x,y,w,h    (also resolve what is inside that screen rectangle)",
            RequiresMainThread = true,
            RelatedCommands = new[] { "INSPECTOR", "LOG", "PLAYMODE", "SCREENSHOT" })]
        public static string Context(string data)
        {
            var args = CommandArgs.Parse(data,
                new[] { "hierarchy", "refs", "brief", "no-console", "children" },
                new[] { "rect" });

            var sb = new StringBuilder();

            AppendEditorState(sb);

            // --rect x,y,w,h (physical desktop pixels) — what the screenshot actually shows.
            // Placed before Selection because when it resolves, it is the more specific answer.
            string rectArg = args.Get("rect");
            if (!string.IsNullOrEmpty(rectArg))
            {
                if (TryParseRect(rectArg, out Rect r))
                {
                    try { sb.AppendLine(RegionVisibility.Describe(r)); }
                    catch (Exception ex) { sb.AppendLine($"*(Region analysis failed: {ex.GetType().Name}: {ex.Message})*").AppendLine(); }
                }
                else
                {
                    sb.AppendLine($"*(Could not parse --rect '{rectArg}'; expected x,y,w,h)*").AppendLine();
                }
            }

            AppendSelection(sb, args);

            if (args.Has("hierarchy"))
            {
                string tree = Invoke("INSPECTOR", "");
                if (!string.IsNullOrWhiteSpace(tree))
                {
                    sb.AppendLine("## Scene hierarchy").AppendLine();
                    sb.AppendLine("```").AppendLine(tree.TrimEnd()).AppendLine("```").AppendLine();
                }
            }

            if (!args.Has("no-console"))
            {
                string logs = Invoke("LOG", "errors");
                if (!string.IsNullOrWhiteSpace(logs) && !LooksEmpty(logs))
                {
                    sb.AppendLine("## Console errors").AppendLine();
                    sb.AppendLine("```").AppendLine(logs.TrimEnd()).AppendLine("```").AppendLine();
                }
            }

            return Response.Success(sb.ToString().TrimEnd());
        }

        // ─── Editor / game state ──────────────────────────────────────

        static void AppendEditorState(StringBuilder sb)
        {
            sb.AppendLine("## Editor state").AppendLine();

            // Prefab Mode first: while it is open the "current" hierarchy is the prefab's contents,
            // and SceneManager.GetActiveScene() still reports the scene you left — which would
            // describe something the user is not looking at.
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
            {
                sb.AppendLine($"- Editing prefab (Prefab Mode): `{stage.assetPath}`");
            }
            else
            {
                var active = SceneManager.GetActiveScene();
                sb.AppendLine($"- Active scene: `{(string.IsNullOrEmpty(active.path) ? active.name : active.path)}`"
                              + (active.isDirty ? "  *(unsaved changes)*" : ""));

                // Additively-loaded scenes are part of "current state" and invisible if you only
                // ask for the active one.
                if (SceneManager.sceneCount > 1)
                {
                    var others = new List<string>();
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var s = SceneManager.GetSceneAt(i);
                        if (s == active) continue;
                        others.Add($"`{(string.IsNullOrEmpty(s.path) ? s.name : s.path)}`"
                                   + (s.isLoaded ? "" : " (not loaded)"));
                    }
                    if (others.Count > 0)
                        sb.AppendLine($"- Also open: {string.Join(", ", others)}");
                }
            }

            string play = !EditorApplication.isPlaying ? "stopped"
                        : EditorApplication.isPaused ? "playing (paused)"
                        : "playing";
            sb.AppendLine($"- Play mode: {play}");
            if (EditorApplication.isCompiling) sb.AppendLine("- Compiling: yes");
            if (EditorApplication.isUpdating) sb.AppendLine("- Importing assets: yes");
            sb.AppendLine($"- Unity: {Application.unityVersion}   Product: {Application.productName}");
            sb.AppendLine($"- Captured: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
        }

        // ─── Selection ────────────────────────────────────────────────

        static void AppendSelection(StringBuilder sb, CommandArgs args)
        {
            var gos = Selection.gameObjects;
            if (gos == null || gos.Length == 0)
            {
                // Say so explicitly. A silent omission reads as "nothing was wrong with the
                // selection" rather than "nothing was selected".
                sb.AppendLine("## Selection").AppendLine();
                sb.AppendLine("Nothing selected in the Hierarchy when this was captured.").AppendLine();

                // An asset selected in the Project window is still a selection worth reporting.
                var assets = Selection.objects;
                if (assets != null && assets.Length > 0)
                {
                    var paths = new List<string>();
                    foreach (var o in assets)
                    {
                        string p = AssetDatabase.GetAssetPath(o);
                        if (!string.IsNullOrEmpty(p)) paths.Add($"`{p}`");
                    }
                    if (paths.Count > 0)
                        sb.AppendLine($"Selected in Project window: {string.Join(", ", paths)}").AppendLine();
                }
                return;
            }

            string flags = args.Has("brief") ? " --brief" : args.Has("refs") ? " --refs" : "";
            if (args.Has("children")) flags += " --children";

            sb.AppendLine("## Selection").AppendLine();
            foreach (var go in gos)
            {
                if (go == null) continue;

                // Selection.gameObjects also returns PREFAB ASSETS picked in the Project window.
                // Those have no scene, so a hierarchy path is meaningless to INSPECTOR ("GameObject
                // not found") — and activeInHierarchy is false for them, which would mislabel an
                // asset as an inactive scene object. Address them by asset path instead.
                bool inScene = go.scene.IsValid() && !string.IsNullOrEmpty(go.scene.name);
                string target, label;
                if (inScene)
                {
                    target = PathOf(go.transform);
                    label = $"`{target}`" + (go.activeInHierarchy ? "" : "  *(inactive)*");
                }
                else
                {
                    string assetPath = AssetDatabase.GetAssetPath(go);
                    if (string.IsNullOrEmpty(assetPath))
                    {
                        // Neither a scene object nor an asset — a preview-scene instance or similar.
                        sb.AppendLine($"### `{go.name}`  *(not addressable: no scene and no asset path)*").AppendLine();
                        continue;
                    }
                    target = assetPath;
                    label = $"`{assetPath}`  *(prefab asset, selected in Project)*";
                }

                sb.AppendLine("### " + label).AppendLine();

                string dump = Invoke("INSPECTOR", target + flags);
                sb.AppendLine("```");
                sb.AppendLine(string.IsNullOrWhiteSpace(dump) ? "(INSPECTOR returned nothing)" : dump.TrimEnd());
                sb.AppendLine("```").AppendLine();
            }
        }

        static bool TryParseRect(string s, out Rect r)
        {
            r = default;
            var parts = s.Split(new[] { ',', 'x', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4) return false;
            if (!float.TryParse(parts[0], out float x) || !float.TryParse(parts[1], out float y)
             || !float.TryParse(parts[2], out float w) || !float.TryParse(parts[3], out float h)) return false;
            if (w <= 0 || h <= 0) return false;
            r = new Rect(x, y, w, h);
            return true;
        }

        [BridgeCommand("VISIBLE", "What is inside a screen rectangle: editor views, and the Scene/Game objects under it",
            Category = "Core",
            Usage = "VISIBLE 1200,400,640,360      (x,y,w,h in physical desktop pixels)",
            RequiresMainThread = true,
            RelatedCommands = new[] { "CONTEXT", "SCREENSHOT", "INSPECTOR" })]
        public static string Visible(string data)
        {
            if (!TryParseRect((data ?? "").Trim(), out Rect r))
                return Response.Error("VISIBLE needs a rectangle: VISIBLE x,y,w,h (physical desktop pixels)");
            return Response.Success(RegionVisibility.Describe(r));
        }

        static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        // ─── Cross-command invocation ─────────────────────────────────

        /// <summary>
        /// Call another registered command through the registry's MethodInfo. INSPECTOR lives in
        /// Commands.Component; referencing that assembly from Commands.Core would invert the
        /// dependency and drag it into every Core recompile, for one call. Already on the main
        /// thread here (RequiresMainThread), so this is a direct synchronous invoke.
        /// </summary>
        internal static string Invoke(string name, string data)
        {
            try
            {
                var info = CommandRegistry.GetCommand(name);
                if (info?.Method == null) return null;
                var ps = info.Method.GetParameters();
                object[] a = ps.Length == 0 ? Array.Empty<object>() : new object[] { data };
                return info.Method.Invoke(info.Instance, a) as string;
            }
            catch (TargetInvocationException ex)
            {
                return $"({name} failed: {ex.InnerException?.Message ?? ex.Message})";
            }
            catch (Exception ex)
            {
                return $"({name} failed: {ex.Message})";
            }
        }

        /// <summary>LOG returns a header even with no matching entries; don't paste an empty block.</summary>
        static bool LooksEmpty(string logs)
        {
            string t = logs.Trim();
            return t.Length == 0
                || t.IndexOf("no logs", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("0 entries", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
