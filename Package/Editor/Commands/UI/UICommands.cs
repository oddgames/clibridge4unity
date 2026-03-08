using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using TMPro;

namespace clibridge4unity
{
    public static class UICommands
    {
        [BridgeCommand("UI_DISCOVER", "Discover UI assets: sprites, fonts, UI prefabs, and scenes",
            Category = "UI",
            Usage = "UI_DISCOVER\n" +
                    "  UI_DISCOVER sprites           - List sprite folders with counts\n" +
                    "  UI_DISCOVER prefabs           - List prefabs (Canvas screens + RectTransform elements)\n" +
                    "  UI_DISCOVER scenes            - List scenes containing UI\n" +
                    "  UI_DISCOVER fonts             - List available fonts\n" +
                    "  UI_DISCOVER sprites:Icons/128  - List sprites in a specific folder",
            RequiresMainThread = true)]
        public static string Discover(string data)
        {
            try
            {
                string filter = data?.Trim().ToLowerInvariant() ?? "";

                if (string.IsNullOrEmpty(filter))
                    return DiscoverAll();
                if (filter == "sprites")
                    return DiscoverSprites(null);
                if (filter.StartsWith("sprites:"))
                    return DiscoverSprites(filter.Substring("sprites:".Length).Trim());
                if (filter == "prefabs")
                    return DiscoverUIPrefabs();
                if (filter == "scenes")
                    return DiscoverUIScenes();
                if (filter == "fonts")
                    return DiscoverFonts();

                return Response.Error($"Unknown filter: {data}. Use: sprites, prefabs, scenes, fonts, or sprites:<folder>");
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        private static string DiscoverAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== UI Asset Discovery ===");
            sb.AppendLine();

            // --- Sprite texture folders ---
            sb.AppendLine("## Sprites");
            var spriteGuids = AssetDatabase.FindAssets("t:Sprite", new[] { "Assets" });
            var spritePaths = spriteGuids.Select(g => AssetDatabase.GUIDToAssetPath(g)).ToArray();
            var folderGroups = spritePaths
                .GroupBy(p => Path.GetDirectoryName(p).Replace("\\", "/"))
                .OrderByDescending(g => g.Count())
                .ToList();

            // Skip icon size duplicates (32/64/256/512) if 128 exists — they're the same icons at different sizes
            var iconFolders = folderGroups.Where(g => g.Key.Contains("/Icons/")).ToList();
            var nonIconFolders = folderGroups.Where(g => !g.Key.Contains("/Icons/")).ToList();
            var preferredIconFolder = iconFolders.FirstOrDefault(g => g.Key.Contains("/128"));
            var otherIconFolders = iconFolders.Where(g => !g.Key.Contains("/128")).ToList();

            string commonPrefix = FindCommonPrefix(folderGroups.Select(g => g.Key).ToArray());
            if (!string.IsNullOrEmpty(commonPrefix))
                sb.AppendLine($"  Base: {commonPrefix}");

            // Show non-icon folders with all names
            foreach (var group in nonIconFolders)
            {
                string displayPath = !string.IsNullOrEmpty(commonPrefix) && group.Key.StartsWith(commonPrefix)
                    ? group.Key.Substring(commonPrefix.Length)
                    : group.Key;
                var names = group.OrderBy(p => p).Select(p => Path.GetFileNameWithoutExtension(p)).ToArray();
                sb.AppendLine($"  {displayPath}/ ({names.Length})");
                for (int i = 0; i < names.Length; i += 4)
                {
                    var batch = names.Skip(i).Take(4).Select(n => n.PadRight(28));
                    sb.AppendLine($"    {string.Join("", batch)}");
                }
            }

            // Show icons (128px only, note other sizes)
            if (preferredIconFolder != null)
            {
                string displayPath = !string.IsNullOrEmpty(commonPrefix) && preferredIconFolder.Key.StartsWith(commonPrefix)
                    ? preferredIconFolder.Key.Substring(commonPrefix.Length)
                    : preferredIconFolder.Key;
                var names = preferredIconFolder.OrderBy(p => p).Select(p => Path.GetFileNameWithoutExtension(p)).ToArray();
                var otherSizes = otherIconFolders.Select(g =>
                {
                    var match = System.Text.RegularExpressions.Regex.Match(g.Key, @"/(\d+)/?$");
                    return match.Success ? match.Groups[1].Value + "px" : g.Key;
                });
                sb.AppendLine($"  {displayPath}/ ({names.Length}) [also available: {string.Join(", ", otherSizes)}]");
                for (int i = 0; i < names.Length; i += 4)
                {
                    var batch = names.Skip(i).Take(4).Select(n => n.PadRight(28));
                    sb.AppendLine($"    {string.Join("", batch)}");
                }
            }

            sb.AppendLine($"  Total: {spritePaths.Length} sprites in {folderGroups.Count} folders");
            sb.AppendLine();

            // --- Fonts ---
            sb.AppendLine("## Fonts");
            var fontGuids = AssetDatabase.FindAssets("t:Font", new[] { "Assets" });
            var allFontPaths = fontGuids.Select(g => AssetDatabase.GUIDToAssetPath(g)).ToList();
            var tmpFontGuids = AssetDatabase.FindAssets("t:TMP_FontAsset", new[] { "Assets" });
            var tmpFontPaths = tmpFontGuids.Select(g => AssetDatabase.GUIDToAssetPath(g)).ToList();

            // Group fonts by folder, show folder once
            var fontsByFolder = allFontPaths.Select(p => new { path = p, label = "" })
                .Concat(tmpFontPaths.Select(p => new { path = p, label = " (TMP)" }))
                .GroupBy(f => Path.GetDirectoryName(f.path).Replace("\\", "/"));
            foreach (var group in fontsByFolder)
            {
                sb.AppendLine($"  {group.Key}/");
                foreach (var f in group)
                    sb.AppendLine($"    {Path.GetFileName(f.path)}{f.label}");
            }
            sb.AppendLine();

            // --- UI Prefabs ---
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
            var canvasPrefabs = new List<(string path, GameObject go)>();
            var elementPrefabs = new List<(string path, GameObject go)>();

            foreach (var guid in prefabGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                if (prefab.GetComponentInChildren<Canvas>(true) != null)
                    canvasPrefabs.Add((path, prefab));
                else if (prefab.GetComponent<RectTransform>() != null)
                    elementPrefabs.Add((path, prefab));
            }

            sb.AppendLine($"## UI Screen Prefabs — Canvas root ({canvasPrefabs.Count})");
            foreach (var (path, prefab) in canvasPrefabs)
            {
                var canvas = prefab.GetComponentInChildren<Canvas>(true);
                var imageCount = prefab.GetComponentsInChildren<Image>(true).Length;
                var textCount = prefab.GetComponentsInChildren<Text>(true).Length;
                var buttonCount = prefab.GetComponentsInChildren<Button>(true).Length;
                sb.AppendLine($"  {path}  ({canvas.renderMode}, {imageCount} Img, {textCount} Txt, {buttonCount} Btn)");
            }
            if (canvasPrefabs.Count == 0)
                sb.AppendLine("  (none found)");
            sb.AppendLine();

            sb.AppendLine($"## UI Element Prefabs — RectTransform root, place inside Canvas ({elementPrefabs.Count})");
            var elementsByFolder = elementPrefabs
                .GroupBy(e => Path.GetDirectoryName(e.path).Replace("\\", "/"))
                .OrderBy(g => g.Key);
            foreach (var group in elementsByFolder)
            {
                sb.AppendLine($"  {group.Key}/");
                foreach (var (path, prefab) in group.OrderBy(e => e.path))
                {
                    var imageCount = prefab.GetComponentsInChildren<Image>(true).Length;
                    var buttonCount = prefab.GetComponentsInChildren<Button>(true).Length;
                    var tmpCount = prefab.GetComponentsInChildren<TMPro.TMP_Text>(true).Length;
                    var parts = new List<string>();
                    if (imageCount > 0) parts.Add($"{imageCount} Img");
                    if (tmpCount > 0) parts.Add($"{tmpCount} TMP");
                    if (buttonCount > 0) parts.Add($"{buttonCount} Btn");
                    string info = parts.Count > 0 ? $"  ({string.Join(", ", parts)})" : "";
                    sb.AppendLine($"    {Path.GetFileNameWithoutExtension(path)}{info}");
                }
            }
            if (elementPrefabs.Count == 0)
                sb.AppendLine("  (none found)");
            sb.AppendLine();

            // --- Scenes with UI ---
            sb.AppendLine("## Scenes");
            var sceneGuids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" });
            foreach (var guid in sceneGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                try
                {
                    var sceneText = File.ReadAllText(path);
                    bool hasCanvas = sceneText.Contains("m_Component:") && sceneText.Contains("Canvas");
                    sb.AppendLine(hasCanvas ? $"  {path} [UI]" : $"  {path}");
                }
                catch
                {
                    sb.AppendLine($"  {path}");
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static string DiscoverSprites(string subfolder)
        {
            var sb = new StringBuilder();
            string[] searchFolders = { "Assets" };

            if (!string.IsNullOrEmpty(subfolder))
            {
                // Find folders matching the subfolder pattern
                var matchingFolders = AssetDatabase.GetAllAssetPaths()
                    .Where(p => AssetDatabase.IsValidFolder(p) &&
                                p.Replace("\\", "/").Contains(subfolder, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (matchingFolders.Length == 0)
                    return Response.Error($"No folders matching: {subfolder}");

                searchFolders = matchingFolders;
                sb.AppendLine($"Sprites in folders matching '{subfolder}':");
            }
            else
            {
                sb.AppendLine("All sprite folders:");
            }
            sb.AppendLine();

            var spriteGuids = AssetDatabase.FindAssets("t:Sprite", searchFolders);
            var spritePaths = spriteGuids.Select(g => AssetDatabase.GUIDToAssetPath(g)).Distinct().ToArray();

            if (!string.IsNullOrEmpty(subfolder))
            {
                // List individual sprites - just names since folder context is known
                var names = spritePaths.OrderBy(p => p)
                    .Select(p => Path.GetFileNameWithoutExtension(p))
                    .ToArray();

                // Show in compact columns
                for (int i = 0; i < Math.Min(names.Length, 200); i += 4)
                {
                    var batch = names.Skip(i).Take(4).Select(n => n.PadRight(28));
                    sb.AppendLine($"  {string.Join("", batch)}");
                }
                if (names.Length > 200)
                    sb.AppendLine($"  ... and {names.Length - 200} more");
                sb.AppendLine();
                sb.AppendLine($"Total: {spritePaths.Length} sprites");
                sb.AppendLine($"Path: {searchFolders[0]}");
            }
            else
            {
                // Group by folder - find common prefix to abbreviate
                var groups = spritePaths
                    .GroupBy(p => Path.GetDirectoryName(p).Replace("\\", "/"))
                    .OrderByDescending(g => g.Count())
                    .ToList();

                string commonPrefix = FindCommonPrefix(groups.Select(g => g.Key).ToArray());

                if (!string.IsNullOrEmpty(commonPrefix))
                    sb.AppendLine($"Base: {commonPrefix}");

                foreach (var group in groups.Take(30))
                {
                    string displayPath = !string.IsNullOrEmpty(commonPrefix) && group.Key.StartsWith(commonPrefix)
                        ? group.Key.Substring(commonPrefix.Length)
                        : group.Key;
                    sb.AppendLine($"  {displayPath}/ ({group.Count()})");

                    // Show all sprite names in compact columns
                    var names = group.OrderBy(p => p)
                        .Select(p => Path.GetFileNameWithoutExtension(p))
                        .ToArray();
                    for (int i = 0; i < names.Length; i += 4)
                    {
                        var batch = names.Skip(i).Take(4).Select(n => n.PadRight(28));
                        sb.AppendLine($"    {string.Join("", batch)}");
                    }
                }
                sb.AppendLine();
                sb.AppendLine($"Total: {spritePaths.Length} sprites in {groups.Count} folders");
            }

            return sb.ToString().TrimEnd();
        }

        private static string DiscoverUIPrefabs()
        {
            var sb = new StringBuilder();

            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
            var canvasPrefabs = new List<(string path, GameObject go)>();
            var elementPrefabs = new List<(string path, GameObject go)>();

            foreach (var guid in prefabGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                if (prefab.GetComponentInChildren<Canvas>(true) != null)
                    canvasPrefabs.Add((path, prefab));
                else if (prefab.GetComponent<RectTransform>() != null)
                    elementPrefabs.Add((path, prefab));
            }

            // --- Canvas prefabs (full UI screens) ---
            sb.AppendLine($"UI Prefabs with Canvas ({canvasPrefabs.Count}):");
            sb.AppendLine();
            foreach (var (path, prefab) in canvasPrefabs)
            {
                var canvas = prefab.GetComponentInChildren<Canvas>(true);
                sb.AppendLine($"  {path}");

                var images = prefab.GetComponentsInChildren<Image>(true);
                var buttons = prefab.GetComponentsInChildren<Button>(true);
                var inputs = prefab.GetComponentsInChildren<InputField>(true);
                var toggles = prefab.GetComponentsInChildren<Toggle>(true);
                var sliders = prefab.GetComponentsInChildren<Slider>(true);
                var tmps = prefab.GetComponentsInChildren<TMPro.TMP_Text>(true);
                var scrollRects = prefab.GetComponentsInChildren<ScrollRect>(true);

                var parts = new List<string>();
                if (images.Length > 0) parts.Add($"{images.Length} Image");
                if (tmps.Length > 0) parts.Add($"{tmps.Length} TMP");
                if (buttons.Length > 0) parts.Add($"{buttons.Length} Button");
                if (inputs.Length > 0) parts.Add($"{inputs.Length} InputField");
                if (toggles.Length > 0) parts.Add($"{toggles.Length} Toggle");
                if (sliders.Length > 0) parts.Add($"{sliders.Length} Slider");
                if (scrollRects.Length > 0) parts.Add($"{scrollRects.Length} ScrollRect");

                sb.AppendLine($"    Canvas: {canvas.renderMode} | {string.Join(", ", parts)}");

                var root = prefab.transform;
                for (int i = 0; i < Mathf.Min(root.childCount, 8); i++)
                    sb.AppendLine($"    - {root.GetChild(i).name}");
                if (root.childCount > 8)
                    sb.AppendLine($"    - ... +{root.childCount - 8} more");
                sb.AppendLine();
            }
            if (canvasPrefabs.Count == 0)
                sb.AppendLine("  (none found)\n");

            // --- Element prefabs (RectTransform root, no Canvas) ---
            sb.AppendLine($"UI Element Prefabs (RectTransform, no Canvas) ({elementPrefabs.Count}):");
            sb.AppendLine();
            var byFolder = elementPrefabs
                .GroupBy(e => Path.GetDirectoryName(e.path).Replace("\\", "/"))
                .OrderBy(g => g.Key);
            foreach (var group in byFolder)
            {
                sb.AppendLine($"  {group.Key}/");
                foreach (var (path, prefab) in group.OrderBy(e => e.path))
                {
                    var images = prefab.GetComponentsInChildren<Image>(true);
                    var buttons = prefab.GetComponentsInChildren<Button>(true);
                    var tmps = prefab.GetComponentsInChildren<TMPro.TMP_Text>(true);
                    var toggles = prefab.GetComponentsInChildren<Toggle>(true);
                    var sliders = prefab.GetComponentsInChildren<Slider>(true);

                    var parts = new List<string>();
                    if (images.Length > 0) parts.Add($"{images.Length} Img");
                    if (tmps.Length > 0) parts.Add($"{tmps.Length} TMP");
                    if (buttons.Length > 0) parts.Add($"{buttons.Length} Btn");
                    if (toggles.Length > 0) parts.Add($"{toggles.Length} Toggle");
                    if (sliders.Length > 0) parts.Add($"{sliders.Length} Slider");

                    string info = parts.Count > 0 ? $"  ({string.Join(", ", parts)})" : "";
                    string name = Path.GetFileNameWithoutExtension(path);

                    // Show root children as structure hint
                    var root = prefab.transform;
                    var childNames = Enumerable.Range(0, Mathf.Min(root.childCount, 5))
                        .Select(i => root.GetChild(i).name);
                    string children = root.childCount > 0 ? $"  [{string.Join(", ", childNames)}{(root.childCount > 5 ? ", ..." : "")}]" : "";

                    sb.AppendLine($"    {name}{info}{children}");
                }
                sb.AppendLine();
            }
            if (elementPrefabs.Count == 0)
                sb.AppendLine("  (none found)");

            return sb.ToString().TrimEnd();
        }

        private static string DiscoverUIScenes()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Scenes with UI content:");
            sb.AppendLine();

            var sceneGuids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" });
            int uiSceneCount = 0;

            foreach (var guid in sceneGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                try
                {
                    var sceneText = File.ReadAllText(path);
                    bool hasCanvas = sceneText.Contains("Canvas");
                    bool hasEventSystem = sceneText.Contains("EventSystem");

                    var indicators = new List<string>();
                    if (hasCanvas) indicators.Add("Canvas");
                    if (hasEventSystem) indicators.Add("EventSystem");
                    if (sceneText.Contains("GraphicRaycaster")) indicators.Add("GraphicRaycaster");

                    if (indicators.Count > 0)
                    {
                        uiSceneCount++;
                        sb.AppendLine($"  {path}");
                        sb.AppendLine($"    UI indicators: {string.Join(", ", indicators)}");
                    }
                }
                catch { /* skip unreadable scenes */ }
            }

            if (uiSceneCount == 0)
                sb.AppendLine("  (no scenes with UI found)");

            // Also list all scenes
            if (sceneGuids.Length > uiSceneCount)
            {
                sb.AppendLine();
                sb.AppendLine("Other scenes:");
                foreach (var guid in sceneGuids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    try
                    {
                        var sceneText = File.ReadAllText(path);
                        if (!sceneText.Contains("Canvas"))
                            sb.AppendLine($"  {path}");
                    }
                    catch
                    {
                        sb.AppendLine($"  {path}");
                    }
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static string DiscoverFonts()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Available Fonts:");
            sb.AppendLine();

            // Regular fonts
            sb.AppendLine("## Unity Fonts (TTF/OTF)");
            var fontGuids = AssetDatabase.FindAssets("t:Font", new[] { "Assets" });
            foreach (var guid in fontGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var font = AssetDatabase.LoadAssetAtPath<Font>(path);
                if (font != null)
                    sb.AppendLine($"  {font.name}  ({path})");
            }
            if (fontGuids.Length == 0)
                sb.AppendLine("  (none)");
            sb.AppendLine();

            // TMP fonts
            sb.AppendLine("## TextMeshPro Font Assets");
            var tmpGuids = AssetDatabase.FindAssets("t:TMP_FontAsset", new[] { "Assets" });
            foreach (var guid in tmpGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                sb.AppendLine($"  {path}");
            }
            // Also check Packages for TMP defaults
            var tmpPackageGuids = AssetDatabase.FindAssets("t:TMP_FontAsset", new[] { "Packages" });
            if (tmpPackageGuids.Length > 0)
            {
                sb.AppendLine($"  + {tmpPackageGuids.Length} built-in TMP fonts (in Packages/)");
            }
            if (tmpGuids.Length == 0 && tmpPackageGuids.Length == 0)
                sb.AppendLine("  (none)");

            return sb.ToString().TrimEnd();
        }

        private static string FindCommonPrefix(string[] paths)
        {
            if (paths == null || paths.Length == 0) return "";
            if (paths.Length == 1) return paths[0] + "/";

            var first = paths[0];
            int prefixLen = first.Length;
            foreach (var p in paths.Skip(1))
            {
                prefixLen = Math.Min(prefixLen, p.Length);
                for (int i = 0; i < prefixLen; i++)
                {
                    if (first[i] != p[i])
                    {
                        prefixLen = i;
                        break;
                    }
                }
            }

            // Trim back to last /
            var prefix = first.Substring(0, prefixLen);
            int lastSlash = prefix.LastIndexOf('/');
            return lastSlash >= 0 ? prefix.Substring(0, lastSlash + 1) : "";
        }
    }
}
