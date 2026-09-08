using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace clibridge4unity
{
    /// <summary>
    /// Answers "what is actually inside this screen rectangle?" for a region screenshot.
    ///
    /// The capture happens outside Unity and arrives as a desktop rectangle in PHYSICAL pixels.
    /// Everything Unity reports — EditorWindow.position included — is in scaled points. On a 4K
    /// display at 175% those differ by 1.75x, so comparing them directly silently resolves to the
    /// wrong view rather than failing. Every conversion here goes through pixelsPerPoint for that
    /// reason.
    ///
    /// Each view answers differently, and the method matters more than the answer:
    ///   Scene view — camera projection over renderer bounds. HandleUtility.PickRectObjects would
    ///                be ideal and is NOT usable: measured, it throws NullReferenceException from a
    ///                bridge command and still throws after Handles.SetCamera, because it needs
    ///                state that exists only inside the Scene view's own OnGUI.
    ///   Game view  — the rect is mapped through GameView's letterbox into game pixels, then
    ///                resolved three ways: uGUI and UI Toolkit by RECT OVERLAP (not raycast — a
    ///                raycast only sees raycastTarget=true, missing every decorative image the
    ///                screenshot is usually about), and world objects by physics rays, with
    ///                renderer-bounds projection as a second tier for collider-less geometry.
    ///   Inspector / Hierarchy — not parsed. IMGUI leaves no queryable model, and the useful
    ///                answer is the current Selection, which CONTEXT already reports.
    /// </summary>
    public static class RegionVisibility
    {
        // Detail is what makes the prompt huge — one INSPECTOR dump of a rich component can run to
        // thousands of lines. Past roughly this much, a pasted prompt stops being readable and the
        // signal drowns; the full text is on disk either way.
        const int DetailByteBudget = 8000;

        const int RaySamples = 12;      // 12x12 grid across the region — 144 rays, cheap and enough
                                        // to separate "fills the region" from "clipped a corner"

        public static string Describe(Rect physicalRect)
        {
            var sb = new StringBuilder();
            float ppp = Mathf.Max(0.01f, EditorGUIUtility.pixelsPerPoint);
            var pts = new Rect(physicalRect.x / ppp, physicalRect.y / ppp,
                               physicalRect.width / ppp, physicalRect.height / ppp);

            sb.AppendLine("## Visible in the captured region").AppendLine();
            sb.AppendLine($"- Region: {physicalRect.width:0}x{physicalRect.height:0} px at "
                          + $"({physicalRect.x:0}, {physicalRect.y:0})   [{pts.width:0}x{pts.height:0} pt @ {ppp:0.##}x]");

            var overlaps = FindOverlappingWindows(pts);
            if (overlaps.Count == 0)
            {
                sb.AppendLine("- No Unity editor window overlaps this region — it captured something else.");
                return sb.ToString();
            }

            sb.AppendLine("- Editor views under it: "
                + string.Join(", ", overlaps.Select(o => $"{Pretty(o.window)} ({o.coverage:0}%)")));
            sb.AppendLine();

            var top = overlaps[0];
            string typeName = top.window.GetType().Name;

            _detail.Clear();
            if (typeName.Contains("SceneView"))
                DescribeSceneView(sb, top.window as SceneView, pts);
            else if (typeName.Contains("GameView"))
                DescribeGameView(sb, top.window, pts);
            else
                DescribeEditorChrome(sb, typeName);

            AppendDetail(sb);
            return sb.ToString();
        }

        // Objects the region resolved to, most-covering first — filled by whichever describer ran.
        static readonly List<GameObject> _detail = new List<GameObject>();

        /// <summary>
        /// Serialized fields for the objects the region actually contains. Listing a name and a
        /// component list identifies them; it does not say what is wrong with them, which is the
        /// reason for taking the screenshot. Capped at three, because a wide region can resolve to
        /// dozens and a prompt full of transforms buries the one that matters.
        /// </summary>
        static void AppendDetail(StringBuilder sb)
        {
            if (_detail.Count == 0) return;

            sb.AppendLine("#### Details of what the region shows").AppendLine();
            int shown = 0;
            int budget = DetailByteBudget;
            foreach (var go in _detail)
            {
                if (go == null) continue;
                if (shown++ >= 3 || budget <= 0)
                {
                    int left = _detail.Count - (shown - 1);
                    if (left > 0)
                        sb.AppendLine($"*({left} further object(s) listed above without field detail.)*").AppendLine();
                    break;
                }
                string path = PathOf(go.transform);
                sb.AppendLine($"##### `{path}`").AppendLine();
                string dump = ContextCommand.Invoke("INSPECTOR", path);
                if (string.IsNullOrWhiteSpace(dump)) dump = "(INSPECTOR returned nothing)";
                dump = dump.TrimEnd();
                if (dump.Length > budget)
                {
                    dump = dump.Substring(0, Mathf.Max(0, budget))
                         + "\n… truncated — full detail is in the .context.md file beside the capture.";
                }
                budget -= dump.Length;
                sb.AppendLine("```");
                sb.AppendLine(dump);
                sb.AppendLine("```").AppendLine();
            }
        }

        // ─── Which views does the rect cover ──────────────────────────

        struct Overlap { public EditorWindow window; public float coverage; }

        static List<Overlap> FindOverlappingWindows(Rect pts)
        {
            var result = new List<Overlap>();
            float area = Mathf.Max(1f, pts.width * pts.height);

            foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (w == null) continue;
                Rect wp;
                try { wp = w.position; } catch { continue; }
                if (wp.width <= 0 || wp.height <= 0) continue;

                // Docked tabs share a rect — Scene and Game sit on top of each other. Without this
                // a region aimed at the Game view resolves to the Scene view behind it, at an
                // equally convincing 100% coverage. Only the selected tab is actually on screen.
                if (!IsFrontmostTab(w)) continue;

                float ox = Mathf.Max(0, Mathf.Min(pts.xMax, wp.xMax) - Mathf.Max(pts.xMin, wp.xMin));
                float oy = Mathf.Max(0, Mathf.Min(pts.yMax, wp.yMax) - Mathf.Max(pts.yMin, wp.yMin));
                if (ox <= 0 || oy <= 0) continue;

                result.Add(new Overlap { window = w, coverage = 100f * (ox * oy) / area });
            }

            result.Sort((a, b) => b.coverage.CompareTo(a.coverage));
            return result;
        }

        /// <summary>
        /// Is this window the selected tab of its dock area? Unity exposes no public API for it,
        /// so this reads m_Parent's selected index — the same reflection the WINDOWS command uses.
        /// A window with no dock area (floating, or a utility window) counts as visible.
        /// </summary>
        static bool IsFrontmostTab(EditorWindow w)
        {
            try
            {
                const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
                var parentField = typeof(EditorWindow).GetField("m_Parent", BF);
                var dockArea = parentField?.GetValue(w);
                if (dockArea == null) return true;

                var selectedProp = dockArea.GetType().GetProperty("selected",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var panesField = dockArea.GetType().GetField("m_Panes", BF);
                if (selectedProp == null || panesField == null) return true;

                int idx = (int)selectedProp.GetValue(dockArea);
                var panes = panesField.GetValue(dockArea) as System.Collections.IList;
                if (panes == null || idx < 0 || idx >= panes.Count) return true;

                return (panes[idx] as EditorWindow) == w;
            }
            catch { return true; }   // unknown layout — better to include than to lose the view
        }

        static string Pretty(EditorWindow w)
        {
            string n = w.GetType().Name;
            switch (n)
            {
                case "SceneView": return "Scene";
                case "GameView": return "Game";
                case "InspectorWindow": return "Inspector";
                case "SceneHierarchyWindow": return "Hierarchy";
                case "ConsoleWindow": return "Console";
                case "ProjectBrowser": return "Project";
                default: return n;
            }
        }

        // ─── Scene view ───────────────────────────────────────────────

        static void DescribeSceneView(StringBuilder sb, SceneView sv, Rect pts)
        {
            if (sv == null) { sb.AppendLine("Scene view could not be resolved."); return; }

            // Rect relative to the view, in its GUI space.
            Rect local = new Rect(pts.x - sv.position.x, pts.y - sv.position.y, pts.width, pts.height);

            // HandleUtility.PickRectObjects is NOT used, despite being the ideal API. Measured:
            // it throws NullReferenceException when called from a bridge command, and still throws
            // after Handles.SetCamera(sv.camera) — it depends on state that only exists inside the
            // Scene view's own OnGUI. Getting there needs a duringSceneGui hook plus a repaint,
            // which cannot complete inside one synchronous command. Projection it is.
            var cam = sv.camera;
            if (cam == null) { sb.AppendLine("Scene view has no camera."); return; }

            var picked = ProjectRenderers(cam, local, sv.position.height).ToArray();
            const string method = "camera projection over renderer bounds — potentially visible, ignores occlusion";

            sb.AppendLine($"### Scene view objects").AppendLine();
            sb.AppendLine($"*Method: {method}*").AppendLine();
            if (picked.Length == 0) { sb.AppendLine("Nothing in that region."); return; }

            foreach (var go in picked.Take(40))
                sb.AppendLine($"- `{PathOf(go.transform)}`  [{Components(go)}]");
            _detail.AddRange(picked.Take(3));
            if (picked.Length > 40) sb.AppendLine($"- …and {picked.Length - 40} more");
            sb.AppendLine();
        }

        // ─── Game view ────────────────────────────────────────────────

        static void DescribeGameView(StringBuilder sb, EditorWindow gv, Rect pts)
        {
            sb.AppendLine("### Game view contents").AppendLine();

            if (!TryMapToGamePixels(gv, pts, out Rect gameRect, out string why))
            {
                sb.AppendLine($"*(Could not map the region into game pixels: {why}. "
                              + "GameView's layout properties are internal and change between Unity versions.)*");
                return;
            }

            sb.AppendLine($"*Region in game pixels: {gameRect.width:0}x{gameRect.height:0} "
                          + $"at ({gameRect.x:0}, {gameRect.y:0}) of {Screen.width}x{Screen.height}*").AppendLine();

            DescribeUGUI(sb, gameRect);
            DescribeUIToolkit(sb, gameRect);
            DescribeWorld(sb, gameRect);
        }

        /// <summary>
        /// Desktop points → game pixels, through GameView's letterbox. targetInView is the drawn
        /// target's rect inside the view; gameMouseScale converts view points to game pixels — the
        /// same numbers Unity uses to place the mouse cursor in a running game.
        /// </summary>
        static bool TryMapToGamePixels(EditorWindow gv, Rect pts, out Rect gameRect, out string why)
        {
            gameRect = default; why = null;
            try
            {
                var t = gv.GetType();
                const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

                var targetInViewProp = t.GetProperty("targetInView", BF);
                var scaleProp = t.GetProperty("gameMouseScale", BF);
                if (targetInViewProp == null || scaleProp == null) { why = "targetInView/gameMouseScale missing"; return false; }

                var targetInView = (Rect)targetInViewProp.GetValue(gv);
                float scale = Convert.ToSingle(scaleProp.GetValue(gv));
                if (scale <= 0f) { why = "gameMouseScale is zero"; return false; }

                // View-local point → subtract where the target is drawn → multiply into game pixels.
                float lx = pts.x - gv.position.x - targetInView.x;
                float ly = pts.y - gv.position.y - targetInView.y;

                gameRect = new Rect(lx * scale, ly * scale, pts.width * scale, pts.height * scale);

                // Clamp to the target and bail if the overlap is empty — the region was over the
                // letterbox bars or the tab strip, not the rendered game.
                float x0 = Mathf.Clamp(gameRect.xMin, 0, Screen.width);
                float y0 = Mathf.Clamp(gameRect.yMin, 0, Screen.height);
                float x1 = Mathf.Clamp(gameRect.xMax, 0, Screen.width);
                float y1 = Mathf.Clamp(gameRect.yMax, 0, Screen.height);
                if (x1 - x0 < 1f || y1 - y0 < 1f) { why = "region falls outside the rendered game area"; return false; }

                gameRect = new Rect(x0, y0, x1 - x0, y1 - y0);
                return true;
            }
            catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        /// <summary>
        /// uGUI elements under the region, by projecting each Graphic's RectTransform corners into
        /// screen space and intersecting rectangles.
        ///
        /// Deliberately NOT GraphicRaycaster: a raycast only finds graphics with raycastTarget =
        /// true, so every decorative Image, background and label — the things a UI screenshot is
        /// usually ABOUT — is invisible to it. Rect overlap also gives exact coverage instead of
        /// sampled approximation, and needs no EventSystem (measured as null outside play).
        /// </summary>
        static void DescribeUGUI(StringBuilder sb, Rect gameRect)
        {
            var graphics = UnityEngine.Object.FindObjectsByType<Graphic>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (graphics == null || graphics.Length == 0) return;

            var hits = new List<(GameObject go, float pct, string extra)>();
            float regionArea = Mathf.Max(1f, gameRect.width * gameRect.height);

            foreach (var g in graphics)
            {
                if (g == null || !g.isActiveAndEnabled) continue;
                var canvas = g.canvas;
                if (canvas == null) continue;

                if (!TryScreenRect(g.rectTransform, canvas, out Rect r)) continue;
                if (!r.Overlaps(gameRect, true)) continue;

                float ox = Mathf.Min(r.xMax, gameRect.xMax) - Mathf.Max(r.xMin, gameRect.xMin);
                float oy = Mathf.Min(r.yMax, gameRect.yMax) - Mathf.Max(r.yMin, gameRect.yMin);
                if (ox <= 0 || oy <= 0) continue;

                // Flag the thing that silently breaks clicks — a raycast target sitting over a
                // button is the single most common "why is this not clickable".
                string extra = g.raycastTarget ? "raycastTarget" : "";
                hits.Add((g.gameObject, 100f * (ox * oy) / regionArea, extra));
            }

            if (hits.Count == 0) return;

            sb.AppendLine("#### UI elements (uGUI)").AppendLine();
            foreach (var h in hits.OrderByDescending(h => h.pct).Take(25))
                sb.AppendLine($"- `{PathOf(h.go.transform)}`  ~{h.pct:0}% of region  "
                              + $"[{Components(h.go)}]{(string.IsNullOrEmpty(h.extra) ? "" : "  *" + h.extra + "*")}");
            if (hits.Count > 25) sb.AppendLine($"- …and {hits.Count - 25} more");
            _detail.AddRange(hits.OrderByDescending(h => h.pct).Select(h => h.go));
            sb.AppendLine();
        }

        /// <summary>
        /// A RectTransform's screen rect, top-left origin to match the captured region. Overlay
        /// canvases are already in screen space; the others project through their event camera.
        /// </summary>
        static bool TryScreenRect(RectTransform rt, Canvas canvas, out Rect screen)
        {
            screen = default;
            try
            {
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);

                Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null
                    : (canvas.worldCamera ?? PickGameCamera());

                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                for (int i = 0; i < 4; i++)
                {
                    Vector2 sp = cam == null
                        ? new Vector2(corners[i].x, corners[i].y)
                        : RectTransformUtility.WorldToScreenPoint(cam, corners[i]);
                    minX = Mathf.Min(minX, sp.x); maxX = Mathf.Max(maxX, sp.x);
                    minY = Mathf.Min(minY, sp.y); maxY = Mathf.Max(maxY, sp.y);
                }
                if (maxX - minX <= 0 || maxY - minY <= 0) return false;

                // Screen space is bottom-left origin; the region is top-left.
                screen = new Rect(minX, Screen.height - maxY, maxX - minX, maxY - minY);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// UI Toolkit elements under the region. worldBound is the panel-space rect of an element,
        /// which is the direct analogue of the uGUI treatment above — no picking, no event system.
        /// </summary>
        static void DescribeUIToolkit(StringBuilder sb, Rect gameRect)
        {
            var docs = UnityEngine.Object.FindObjectsByType<UIDocument>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (docs == null || docs.Length == 0) return;

            var hits = new List<(string name, float pct, string cls)>();
            float regionArea = Mathf.Max(1f, gameRect.width * gameRect.height);

            foreach (var doc in docs)
            {
                var root = doc?.rootVisualElement;
                if (root == null) continue;

                foreach (var el in root.Query<VisualElement>().ToList())
                {
                    if (el == null || el.resolvedStyle.display == DisplayStyle.None) continue;
                    Rect wb;
                    try { wb = el.worldBound; } catch { continue; }
                    if (wb.width <= 0 || wb.height <= 0 || !wb.Overlaps(gameRect, true)) continue;

                    float ox = Mathf.Min(wb.xMax, gameRect.xMax) - Mathf.Max(wb.xMin, gameRect.xMin);
                    float oy = Mathf.Min(wb.yMax, gameRect.yMax) - Mathf.Max(wb.yMin, gameRect.yMin);
                    if (ox <= 0 || oy <= 0) continue;

                    string id = !string.IsNullOrEmpty(el.name) ? "#" + el.name : el.GetType().Name;
                    string cls = el.GetClasses() != null ? string.Join(".", el.GetClasses()) : "";
                    hits.Add((id, 100f * (ox * oy) / regionArea, cls));
                }
            }

            if (hits.Count == 0) return;
            sb.AppendLine("#### UI Toolkit elements").AppendLine();
            foreach (var h in hits.OrderByDescending(h => h.pct).Take(25))
                sb.AppendLine($"- `{h.name}`  ~{h.pct:0}% of region"
                              + (string.IsNullOrEmpty(h.cls) ? "" : $"  .{h.cls}"));
            if (hits.Count > 25) sb.AppendLine($"- …and {hits.Count - 25} more");
            sb.AppendLine();
        }

        /// <summary>3D/2D objects under the region, by raycasting the rendering camera.</summary>
        static void DescribeWorld(StringBuilder sb, Rect gameRect)
        {
            var cam = PickGameCamera();
            if (cam == null) { sb.AppendLine("- No enabled camera to raycast."); return; }

            if (!Application.isPlaying) Physics.SyncTransforms();

            var counts = new Dictionary<GameObject, int>();
            foreach (var p in SamplePoints(gameRect))
            {
                var ray = cam.ScreenPointToRay(new Vector3(p.x, FlipY(p).y, 0f));
                if (Physics.Raycast(ray, out RaycastHit hit, cam.farClipPlane))
                {
                    var go = hit.collider.gameObject;
                    counts.TryGetValue(go, out int c);
                    counts[go] = c + 1;
                }
            }

            // Second tier: renderers with no collider are invisible to physics but perfectly
            // visible on screen — often exactly what was screenshotted. Projected bounds cannot
            // see occlusion, so these are reported separately rather than merged into the
            // confirmed hits, and only when they are not already accounted for.
            var projected = ProjectRenderers(cam, gameRect, Screen.height)
                            .Where(go => go != null && !counts.ContainsKey(go))
                            .Distinct()
                            .ToList();

            sb.AppendLine($"#### World objects (camera `{cam.name}`)").AppendLine();
            if (counts.Count == 0 && projected.Count == 0)
            {
                sb.AppendLine("- Nothing found: no physics hits and no renderer bounds meet the region.");
            }
            else if (counts.Count == 0)
            {
                sb.AppendLine("- No physics hits (nothing here has a collider).");
            }
            else
            {
                int total = RaySamples * RaySamples;
                foreach (var kv in counts.OrderByDescending(k => k.Value).Take(25))
                    sb.AppendLine($"- `{PathOf(kv.Key.transform)}`  ~{100f * kv.Value / total:0}% of region  [{Components(kv.Key)}]");
                _detail.AddRange(counts.OrderByDescending(k => k.Value).Select(k => k.Key));
            }

            if (projected.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("*Renderer-only (no collider — potentially visible, occlusion not checked):*");
                foreach (var go in projected.Take(20))
                    sb.AppendLine($"- `{PathOf(go.transform)}`  [{Components(go)}]"
                                  + (RendererDrawn(go) ? "  *drawn last frame*" : ""));
                if (projected.Count > 20) sb.AppendLine($"- …and {projected.Count - 20} more");
                _detail.AddRange(projected.Take(3));
            }
            sb.AppendLine();
        }

        /// <summary>
        /// Renderer.isVisible — was it drawn by any camera last frame. Not proof it is in THIS
        /// region, but it separates "in the frustum on paper" from "actually being rendered",
        /// which is the difference between a real hit and a culled or disabled object.
        /// </summary>
        static bool RendererDrawn(GameObject go)
        {
            var r = go.GetComponent<Renderer>();
            return r != null && r.isVisible;
        }

        /// <summary>Highest-depth enabled camera — what actually ends up on top in the Game view.</summary>
        static Camera PickGameCamera()
        {
            Camera best = null;
            foreach (var c in Camera.allCameras)
            {
                if (c == null || !c.isActiveAndEnabled) continue;
                if (best == null || c.depth > best.depth) best = c;
            }
            return best ?? Camera.main;
        }

        static IEnumerable<Vector2> SamplePoints(Rect r)
        {
            for (int iy = 0; iy < RaySamples; iy++)
                for (int ix = 0; ix < RaySamples; ix++)
                    yield return new Vector2(
                        r.x + r.width * (ix + 0.5f) / RaySamples,
                        r.y + r.height * (iy + 0.5f) / RaySamples);
        }

        /// <summary>Screen-space input is bottom-left origin; the captured rect is top-left.</summary>
        static Vector2 FlipY(Vector2 p) => new Vector2(p.x, Screen.height - p.y);

        // ─── Editor chrome ────────────────────────────────────────────

        static void DescribeEditorChrome(StringBuilder sb, string typeName)
        {
            sb.AppendLine($"### {Pretty2(typeName)}").AppendLine();
            if (typeName.Contains("Inspector") || typeName.Contains("SceneHierarchy"))
                sb.AppendLine("This region covers an editor panel whose contents are IMGUI-drawn and not queryable. "
                              + "What it was showing is the current Selection, reported above.");
            else if (typeName.Contains("Console"))
                sb.AppendLine("This region covers the Console. Its entries are in the console-errors section above.");
            else if (typeName.Contains("ProjectBrowser"))
                sb.AppendLine("This region covers the Project browser — the selected asset is reported above.");
            else
                sb.AppendLine("No object-level detail is available for this view.");
            sb.AppendLine();
        }

        static string Pretty2(string n) =>
            n == "InspectorWindow" ? "Inspector" :
            n == "SceneHierarchyWindow" ? "Hierarchy" :
            n == "ConsoleWindow" ? "Console" :
            n == "ProjectBrowser" ? "Project" : n;

        // ─── Shared helpers ───────────────────────────────────────────

        /// <summary>
        /// Fallback for when the editor's own picking is unavailable: project every renderer's
        /// bounds and keep those whose screen rect meets the region. Frustum-culled, but blind to
        /// occlusion — an object behind a wall still counts, hence "potentially visible".
        /// </summary>
        static List<GameObject> ProjectRenderers(Camera cam, Rect localRect, float viewHeight)
        {
            var found = new List<GameObject>();
            var planes = GeometryUtility.CalculateFrustumPlanes(cam);

            foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (r == null || !r.enabled) continue;
                if (!GeometryUtility.TestPlanesAABB(planes, r.bounds)) continue;

                var b = r.bounds;
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                bool anyInFront = false;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        (i & 1) == 0 ? b.min.x : b.max.x,
                        (i & 2) == 0 ? b.min.y : b.max.y,
                        (i & 4) == 0 ? b.min.z : b.max.z);
                    var sp = cam.WorldToScreenPoint(corner);
                    if (sp.z <= 0) continue;
                    anyInFront = true;
                    // Camera screen space is bottom-left origin; the view rect is top-left.
                    float y = viewHeight - sp.y;
                    minX = Mathf.Min(minX, sp.x); maxX = Mathf.Max(maxX, sp.x);
                    minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
                }
                if (!anyInFront) continue;

                if (localRect.Overlaps(new Rect(minX, minY, maxX - minX, maxY - minY), true))
                    found.Add(r.gameObject);
            }
            return found;
        }

        static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        static string Components(GameObject go)
        {
            var names = go.GetComponents<Component>()
                          .Where(c => c != null && !(c is Transform))
                          .Select(c => c.GetType().Name)
                          .Take(5);
            string s = string.Join(", ", names);
            return string.IsNullOrEmpty(s) ? "no components" : s;
        }
    }
}
