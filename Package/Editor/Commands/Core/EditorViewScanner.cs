using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace clibridge4unity
{
    /// <summary>
    /// Reads the contents of editor panels that a screenshot region can land on, so no view is a
    /// dead end.
    ///
    /// Measured in Unity 6000.3, because the answer differs per window and is not what the docs
    /// imply:
    ///   Inspector — hybrid. 68 UI Toolkit elements alongside 21 IMGUIContainers, so labels and
    ///               values are partially readable via worldBound, and the IMGUI-drawn parts are
    ///               not. The authoritative content is the Selection's INSPECTOR dump; the scan
    ///               adds which fields were actually on screen.
    ///   Hierarchy — NO UI Toolkit tree at all (rootVisualElement has a single child and no
    ///               IMGUIContainer). It draws through OnGUI, so rows come from the internal
    ///               TreeViewController's data source instead, mapped to the region by scrollPos
    ///               and row height.
    ///   Console / Project — likewise opaque; their content is already reachable through LOG and
    ///               the Selection, so they are answered from there rather than scraped.
    ///
    /// All of this is internal API reached by reflection. Every step degrades to null rather than
    /// throwing, because a Unity upgrade renaming one field must not take a screenshot down with it.
    /// </summary>
    internal static class EditorViewScanner
    {
        const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // ─── Hierarchy ────────────────────────────────────────────────

        internal sealed class HierarchyRow
        {
            public int Id;
            public string Name;
            public int Depth;
            public bool InRegion;
            public bool Selected;
        }

        /// <summary>
        /// The rows the Hierarchy is currently displaying, flagged with which fall inside the
        /// region. These are the DISPLAYED rows — collapsed children are absent and a search filter
        /// is already applied — which is what makes them the right answer for "what was on screen"
        /// rather than just re-listing the scene.
        /// </summary>
        internal static List<HierarchyRow> ScanHierarchy(EditorWindow window, Rect regionPts, out string note)
        {
            note = null;
            try
            {
                var sh = window.GetType().GetField("m_SceneHierarchy", BF)?.GetValue(window);
                if (sh == null) { note = "hierarchy internals not found (m_SceneHierarchy)"; return null; }

                var tv = sh.GetType().GetField("m_TreeView", BF)?.GetValue(sh);
                if (tv == null) { note = "hierarchy tree view not found"; return null; }

                var data = tv.GetType().GetProperty("data", BF)?.GetValue(tv);
                var getRows = data?.GetType().GetMethod("GetRows", BF, null, Type.EmptyTypes, null);
                if (getRows?.Invoke(data, null) is not IList rows) { note = "hierarchy rows unavailable"; return null; }

                // Tree area within the window, and how far it is scrolled.
                Rect treeRect = default;
                var posField = sh.GetType().GetField("<position>k__BackingField", BF);
                if (posField?.GetValue(sh) is Rect r) treeRect = r;

                Vector2 scroll = Vector2.zero;
                var state = tv.GetType().GetProperty("state", BF)?.GetValue(tv);
                if (state?.GetType().GetField("scrollPos", BF)?.GetValue(state) is Vector2 sp) scroll = sp;

                float rowHeight = RowHeight(tv);

                // Region in window-local coordinates, then into tree content space.
                float localTop = regionPts.yMin - window.position.y - treeRect.y + scroll.y;
                float localBottom = regionPts.yMax - window.position.y - treeRect.y + scroll.y;
                int firstRow = Mathf.FloorToInt(localTop / rowHeight);
                int lastRow = Mathf.FloorToInt(localBottom / rowHeight);

                var selected = new HashSet<int>(Selection.instanceIDs ?? Array.Empty<int>());
                var result = new List<HierarchyRow>();
                int i = 0;
                foreach (var row in rows)
                {
                    var t = row.GetType();
                    int id = ToInstanceId(t.GetProperty("id", BF)?.GetValue(row));
                    result.Add(new HierarchyRow
                    {
                        Id = id,
                        Name = t.GetProperty("displayName", BF)?.GetValue(row) as string ?? "?",
                        Depth = t.GetProperty("depth", BF)?.GetValue(row) as int? ?? 0,
                        InRegion = i >= firstRow && i <= lastRow,
                        Selected = selected.Contains(id),
                    });
                    i++;
                }
                return result;
            }
            catch (Exception ex) { note = ex.GetType().Name + ": " + ex.Message; return null; }
        }

        /// <summary>
        /// A tree row's id as an instance id.
        ///
        /// In Unity 6.3 `TreeViewItem.id` is no longer an int but `UnityEngine.EntityId`, and it is
        /// not IConvertible — `Convert.ToInt32` throws and `as int?` silently yields null, which is
        /// worse: every row resolves to id 0, so objects never resolve and the selected-row flag is
        /// quietly always false. EntityId does carry `op_Implicit -> Int32` (and an m_Data int), so
        /// unwrap through that, keeping the plain-int path for older versions.
        /// </summary>
        static int ToInstanceId(object idValue)
        {
            if (idValue == null) return 0;
            if (idValue is int direct) return direct;

            var t = idValue.GetType();
            try
            {
                foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public))
                {
                    if (m.Name != "op_Implicit" && m.Name != "op_Explicit") continue;
                    if (m.ReturnType != typeof(int)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == t)
                        return (int)m.Invoke(null, new[] { idValue });
                }

                foreach (var f in t.GetFields(BF))
                    if (f.FieldType == typeof(int))
                        return (int)f.GetValue(idValue);
            }
            catch { }
            return 0;
        }

        /// <summary>Row height, tried by several names across versions; 16pt is Unity's long-standing default.</summary>
        static float RowHeight(object treeView)
        {
            try
            {
                var gui = treeView.GetType().GetProperty("gui", BF)?.GetValue(treeView);
                if (gui != null)
                {
                    foreach (var name in new[] { "k_LineHeight", "lineHeight", "m_LineHeight" })
                    {
                        var f = gui.GetType().GetField(name, BF | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                        if (f?.GetValue(f.IsStatic ? null : gui) is float v && v > 1f) return v;

                        var p = gui.GetType().GetProperty(name, BF | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                        if (p?.GetValue(p.GetGetMethod(true).IsStatic ? null : gui) is float pv && pv > 1f) return pv;
                    }
                }
            }
            catch { }
            return 16f;
        }

        // ─── Inspector ────────────────────────────────────────────────

        /// <summary>
        /// Text actually drawn inside the region by the Inspector's UI Toolkit half. Partial by
        /// nature — anything an IMGUIContainer painted is invisible here — so it complements the
        /// Selection's INSPECTOR dump rather than replacing it.
        /// </summary>
        internal static List<string> ScanTextInRegion(EditorWindow window, Rect regionPts, out int imguiCount)
        {
            imguiCount = 0;
            var found = new List<string>();
            try
            {
                var root = window.rootVisualElement;
                if (root == null) return found;

                // Region relative to the window — worldBound is panel space, whose origin is the
                // window's own top-left.
                var local = new Rect(regionPts.x - window.position.x, regionPts.y - window.position.y,
                                     regionPts.width, regionPts.height);

                var stack = new Stack<VisualElement>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    var el = stack.Pop();
                    for (int i = 0; i < el.childCount; i++) stack.Push(el[i]);

                    if (el is IMGUIContainer) imguiCount++;
                    if (el is not TextElement te || string.IsNullOrWhiteSpace(te.text)) continue;

                    Rect wb;
                    try { wb = el.worldBound; } catch { continue; }
                    if (wb.width <= 0 || wb.height <= 0 || !wb.Overlaps(local, true)) continue;

                    string text = te.text.Replace("\n", " ").Trim();
                    if (text.Length > 120) text = text.Substring(0, 120) + "…";
                    if (!found.Contains(text)) found.Add(text);
                    if (found.Count >= 40) break;
                }
            }
            catch { }
            return found;
        }
    }
}
