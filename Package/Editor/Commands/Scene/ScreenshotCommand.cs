using System;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace clibridge4unity
{
    /// <summary>
    /// Provides editor window layout info for external screenshot capture.
    /// Actual screen capture is done by the CLI using Win32 APIs.
    /// </summary>
    public static class ScreenshotCommand
    {
        [BridgeCommand("WINDOWS", "List open editor windows with positions",
            Category = "Scene",
            Usage = "WINDOWS",
            RequiresMainThread = true)]
        public static string ListWindows()
        {
            try
            {
                var allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();
                var sb = new StringBuilder();
                sb.AppendLine($"windowCount: {allWindows.Length}");
                sb.AppendLine("---");

                foreach (var window in allWindows.OrderBy(w => w.GetType().FullName))
                {
                    var pos = window.position;
                    // Check if this window is the active/visible tab in its dock area
                    bool visible = window.hasFocus;
                    if (!visible)
                    {
                        try
                        {
                            // A docked window is visible if it's the selected tab in its parent
                            var dockAreaField = typeof(EditorWindow).GetField("m_Parent",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (dockAreaField != null)
                            {
                                var dockArea = dockAreaField.GetValue(window);
                                if (dockArea != null)
                                {
                                    var selectedProp = dockArea.GetType().GetProperty("selected",
                                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                    if (selectedProp != null)
                                    {
                                        int selectedIdx = (int)selectedProp.GetValue(dockArea);
                                        var panesField = dockArea.GetType().GetField("m_Panes",
                                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                        if (panesField != null)
                                        {
                                            var panes = panesField.GetValue(dockArea) as System.Collections.IList;
                                            if (panes != null && selectedIdx >= 0 && selectedIdx < panes.Count)
                                            {
                                                visible = (panes[selectedIdx] as EditorWindow) == window;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    sb.AppendLine($"{window.titleContent.text}|{window.GetType().Name}|{(int)pos.x}|{(int)pos.y}|{(int)pos.width}|{(int)pos.height}|{(visible ? "visible" : "hidden")}");
                }

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }
    }
}
