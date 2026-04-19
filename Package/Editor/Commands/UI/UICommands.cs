using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using UnityEditor;
using TMPro;

namespace clibridge4unity
{
    public static class UICommands
    {
        private static readonly string[] DiscoverFlags = { "sprites", "prefabs", "scenes", "fonts", "shaders", "materials", "models", "variants", "ui" };
        private static readonly string[] DiscoverOptions = { "sprites", "materials", "shaders" };

        [BridgeCommand("ASSET_DISCOVER", "Discover project assets by category",
            Category = "Asset",
            Usage = "ASSET_DISCOVER                      - Summary of all categories\n" +
                    "  ASSET_DISCOVER ui                    - UI prefabs, sprites, fonts\n" +
                    "  ASSET_DISCOVER sprites                - Sprite folders with names\n" +
                    "  ASSET_DISCOVER sprites:Icons/128      - Sprites in specific folder\n" +
                    "  ASSET_DISCOVER prefabs                - UI prefabs (Canvas + element)\n" +
                    "  ASSET_DISCOVER scenes                 - Scenes (flags UI content)\n" +
                    "  ASSET_DISCOVER fonts                  - TTF/OTF and TMP font assets\n" +
                    "  ASSET_DISCOVER shaders                - All shaders in project\n" +
                    "  ASSET_DISCOVER shaders:URP            - Shaders matching filter\n" +
                    "  ASSET_DISCOVER materials               - Materials grouped by shader\n" +
                    "  ASSET_DISCOVER materials:Standard      - Materials using specific shader\n" +
                    "  ASSET_DISCOVER models                  - FBX/OBJ with sub-assets (meshes, mats, clips)\n" +
                    "  ASSET_DISCOVER variants                - Prefab variant inheritance chains",
            RequiresMainThread = true,
            RelatedCommands = new[] { "ASSET_SEARCH", "SCREENSHOT" })]
        public static string Discover(string data)
        {
            try
            {
                var args = CommandArgs.Parse(data, DiscoverFlags, DiscoverOptions);
                string prefix = args.WarningPrefix();

                // Options with values
                string spritesFolder = args.Get("sprites");
                if (spritesFolder != null)
                    return prefix + DiscoverSprites(spritesFolder);

                string materialFilter = args.Get("materials");
                if (materialFilter != null)
                    return prefix + DiscoverMaterials(materialFilter);

                string shaderFilter = args.Get("shaders");
                if (shaderFilter != null)
                    return prefix + DiscoverShaders(shaderFilter);

                // Flags
                if (args.Has("ui"))
                    return prefix + DiscoverUI();
                if (args.Has("sprites"))
                    return prefix + DiscoverSprites(null);
                if (args.Has("prefabs"))
                    return prefix + DiscoverUIPrefabs();
                if (args.Has("scenes"))
                    return prefix + DiscoverScenes();
                if (args.Has("fonts"))
                    return prefix + DiscoverFonts();
                if (args.Has("shaders"))
                    return prefix + DiscoverShaders(null);
                if (args.Has("materials"))
                    return prefix + DiscoverMaterials(null);
                if (args.Has("models"))
                    return prefix + DiscoverModels();
                if (args.Has("variants"))
                    return prefix + DiscoverVariants();

                return prefix + DiscoverSummary();
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        // Keep UI_DISCOVER as alias for backwards compatibility
        [BridgeCommand("UI_DISCOVER", "Alias for ASSET_DISCOVER ui",
            Category = "UI",
            Usage = "UI_DISCOVER [sprites|prefabs|scenes|fonts]",
            RequiresMainThread = true)]
        public static string DiscoverLegacy(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return Discover("ui");
            return Discover(data);
        }

        #region Summary

        private static string DiscoverSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Asset Discovery ===");
            sb.AppendLine();

            // Count by type
            int sprites = AssetDatabase.FindAssets("t:Sprite", new[] { "Assets" }).Length;
            int textures = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" }).Length;
            int materials = AssetDatabase.FindAssets("t:Material", new[] { "Assets" }).Length;
            int prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }).Length;
            int scenes = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }).Length;
            int fonts = AssetDatabase.FindAssets("t:Font", new[] { "Assets" }).Length;
            int tmpFonts = AssetDatabase.FindAssets("t:TMP_FontAsset", new[] { "Assets" }).Length;
            int meshes = AssetDatabase.FindAssets("t:Mesh", new[] { "Assets" }).Length;
            int animations = AssetDatabase.FindAssets("t:AnimationClip", new[] { "Assets" }).Length;
            int scripts = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets" }).Length;
            int audioClips = AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets" }).Length;

            // Count shaders in project (not packages)
            int shaders = AssetDatabase.FindAssets("t:Shader", new[] { "Assets" }).Length;

            // Count models (FBX, OBJ, etc.)
            int models = AssetDatabase.FindAssets("t:Model", new[] { "Assets" }).Length;

            // Count UI prefabs and variants
            int uiPrefabs = 0;
            int variants = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;
                if (go.GetComponentInChildren<Canvas>(true) != null || go.GetComponent<RectTransform>() != null)
                    uiPrefabs++;
                if (PrefabUtility.GetPrefabAssetType(go) == PrefabAssetType.Variant)
                    variants++;
            }

            sb.AppendLine("  Category        Count   Drill down with");
            sb.AppendLine("  ─────────────── ─────── ─────────────────────────");
            sb.AppendLine($"  Sprites         {sprites,7}   ASSET_DISCOVER sprites");
            sb.AppendLine($"  Textures        {textures,7}");
            sb.AppendLine($"  Materials       {materials,7}   ASSET_DISCOVER materials");
            sb.AppendLine($"  Shaders         {shaders,7}   ASSET_DISCOVER shaders");
            sb.AppendLine($"  Models          {models,7}   ASSET_DISCOVER models");
            sb.AppendLine($"  Prefabs         {prefabs,7}   ASSET_DISCOVER prefabs");
            sb.AppendLine($"  Variants        {variants,7}   ASSET_DISCOVER variants");
            sb.AppendLine($"  UI Prefabs      {uiPrefabs,7}   ASSET_DISCOVER ui");
            sb.AppendLine($"  Scenes          {scenes,7}   ASSET_DISCOVER scenes");
            sb.AppendLine($"  Fonts           {fonts,7}   ASSET_DISCOVER fonts");
            sb.AppendLine($"  TMP Fonts       {tmpFonts,7}");
            sb.AppendLine($"  Meshes          {meshes,7}");
            sb.AppendLine($"  Animations      {animations,7}");
            sb.AppendLine($"  Audio Clips     {audioClips,7}");
            sb.AppendLine($"  Scripts         {scripts,7}");

            return sb.ToString().TrimEnd();
        }

        #endregion

        #region UI (combined sprites + fonts + prefabs)

        private static string DiscoverUI()
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

            var iconFolders = folderGroups.Where(g => g.Key.Contains("/Icons/")).ToList();
            var nonIconFolders = folderGroups.Where(g => !g.Key.Contains("/Icons/")).ToList();
            var preferredIconFolder = iconFolders.FirstOrDefault(g => g.Key.Contains("/128"));
            var otherIconFolders = iconFolders.Where(g => !g.Key.Contains("/128")).ToList();

            string commonPrefix = FindCommonPrefix(folderGroups.Select(g => g.Key).ToArray());
            if (!string.IsNullOrEmpty(commonPrefix))
                sb.AppendLine($"  Base: {commonPrefix}");

            foreach (var group in nonIconFolders)
            {
                string displayPath = TrimPrefix(group.Key, commonPrefix);
                var names = group.OrderBy(p => p).Select(p => Path.GetFileNameWithoutExtension(p)).ToArray();
                sb.AppendLine($"  {displayPath}/ ({names.Length})");
                AppendColumns(sb, names, 4, 28);
            }

            if (preferredIconFolder != null)
            {
                string displayPath = TrimPrefix(preferredIconFolder.Key, commonPrefix);
                var names = preferredIconFolder.OrderBy(p => p).Select(p => Path.GetFileNameWithoutExtension(p)).ToArray();
                var otherSizes = otherIconFolders.Select(g =>
                {
                    var match = System.Text.RegularExpressions.Regex.Match(g.Key, @"/(\d+)/?$");
                    return match.Success ? match.Groups[1].Value + "px" : g.Key;
                });
                sb.AppendLine($"  {displayPath}/ ({names.Length}) [also available: {string.Join(", ", otherSizes)}]");
                AppendColumns(sb, names, 4, 28);
            }

            sb.AppendLine($"  Total: {spritePaths.Length} sprites in {folderGroups.Count} folders");
            sb.AppendLine();

            // --- Fonts ---
            sb.AppendLine("## Fonts");
            var fontGuids = AssetDatabase.FindAssets("t:Font", new[] { "Assets" });
            var allFontPaths = fontGuids.Select(g => AssetDatabase.GUIDToAssetPath(g)).ToList();
            var tmpFontGuids = AssetDatabase.FindAssets("t:TMP_FontAsset", new[] { "Assets" });
            var tmpFontPaths = tmpFontGuids.Select(g => AssetDatabase.GUIDToAssetPath(g)).ToList();

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

            sb.AppendLine($"## UI Element Prefabs — RectTransform root ({elementPrefabs.Count})");
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
                    var tmpCount = prefab.GetComponentsInChildren<TMP_Text>(true).Length;
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

            // --- Scenes ---
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

        #endregion

        #region Shaders

        private static string DiscoverShaders(string filter)
        {
            var sb = new StringBuilder();

            // Project shaders
            var projectGuids = AssetDatabase.FindAssets("t:Shader", new[] { "Assets" });
            var projectShaders = projectGuids
                .Select(g => AssetDatabase.GUIDToAssetPath(g))
                .Select(p => (path: p, shader: AssetDatabase.LoadAssetAtPath<Shader>(p)))
                .Where(s => s.shader != null)
                .ToList();

            // Built-in/package shaders (enumerate all loaded shaders not in Assets/)
            var allShaders = Resources.FindObjectsOfTypeAll<Shader>()
                .Where(s => !string.IsNullOrEmpty(s.name) && !s.name.StartsWith("Hidden/"))
                .OrderBy(s => s.name)
                .ToList();

            bool hasFilter = !string.IsNullOrEmpty(filter);

            if (hasFilter)
            {
                projectShaders = projectShaders
                    .Where(s => s.shader.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                             || s.path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                allShaders = allShaders
                    .Where(s => s.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            sb.AppendLine(hasFilter ? $"=== Shaders matching '{filter}' ===" : "=== Shaders ===");
            sb.AppendLine();

            if (projectShaders.Count > 0)
            {
                sb.AppendLine($"## Project Shaders ({projectShaders.Count})");
                foreach (var (path, shader) in projectShaders.OrderBy(s => s.shader.name))
                {
                    int passes = shader.passCount;
                    int propCount = ShaderUtil.GetPropertyCount(shader);
                    sb.AppendLine($"  {shader.name}  ({passes} pass{(passes != 1 ? "es" : "")}, {propCount} props)");
                    sb.AppendLine($"    {path}");
                }
                sb.AppendLine();
            }

            // Group built-in shaders by category (first path segment)
            var builtIn = allShaders
                .Where(s => !projectShaders.Any(ps => ps.shader.name == s.name))
                .ToList();

            if (builtIn.Count > 0)
            {
                sb.AppendLine($"## Available Shaders ({builtIn.Count})");
                var grouped = builtIn.GroupBy(s =>
                {
                    int slash = s.name.IndexOf('/');
                    return slash >= 0 ? s.name.Substring(0, slash) : "(Uncategorized)";
                }).OrderBy(g => g.Key);

                foreach (var group in grouped)
                {
                    sb.AppendLine($"  {group.Key}/ ({group.Count()})");
                    foreach (var shader in group.Take(20))
                        sb.AppendLine($"    {shader.name}");
                    if (group.Count() > 20)
                        sb.AppendLine($"    ... +{group.Count() - 20} more");
                }
            }

            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Materials

        private static string DiscoverMaterials(string filter)
        {
            var sb = new StringBuilder();
            var guids = AssetDatabase.FindAssets("t:Material", new[] { "Assets" });

            var mats = guids
                .Select(g => AssetDatabase.GUIDToAssetPath(g))
                .Select(p => (path: p, mat: AssetDatabase.LoadAssetAtPath<Material>(p)))
                .Where(m => m.mat != null && m.mat.shader != null)
                .ToList();

            bool hasFilter = !string.IsNullOrEmpty(filter);

            if (hasFilter)
            {
                mats = mats
                    .Where(m => m.mat.shader.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                             || m.mat.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                             || m.path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            sb.AppendLine(hasFilter ? $"=== Materials matching '{filter}' ===" : "=== Materials ===");
            sb.AppendLine();

            if (mats.Count == 0)
            {
                sb.AppendLine(hasFilter ? $"  No materials matching '{filter}'" : "  No materials found");
                return sb.ToString().TrimEnd();
            }

            // Group by shader
            var byShader = mats.GroupBy(m => m.mat.shader.name).OrderByDescending(g => g.Count());
            foreach (var group in byShader)
            {
                sb.AppendLine($"## {group.Key} ({group.Count()})");
                foreach (var (path, mat) in group.OrderBy(m => m.path).Take(30))
                {
                    var props = new List<string>();

                    // Show key visual properties
                    if (mat.HasProperty("_Color"))
                    {
                        var c = mat.GetColor("_Color");
                        props.Add($"color:#{ColorUtility.ToHtmlStringRGB(c)}");
                    }
                    if (mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") != null)
                        props.Add($"tex:{mat.GetTexture("_MainTex").name}");
                    if (mat.HasProperty("_BaseMap") && mat.GetTexture("_BaseMap") != null)
                        props.Add($"tex:{mat.GetTexture("_BaseMap").name}");
                    if (mat.HasProperty("_Metallic"))
                        props.Add($"metallic:{mat.GetFloat("_Metallic"):F1}");
                    if (mat.HasProperty("_Smoothness"))
                        props.Add($"smooth:{mat.GetFloat("_Smoothness"):F1}");
                    if (mat.renderQueue != (int)RenderQueue.Geometry)
                        props.Add($"queue:{mat.renderQueue}");

                    string info = props.Count > 0 ? $"  ({string.Join(", ", props)})" : "";
                    sb.AppendLine($"  {path}{info}");
                }
                if (group.Count() > 30)
                    sb.AppendLine($"  ... +{group.Count() - 30} more");
                sb.AppendLine();
            }

            sb.AppendLine($"Total: {mats.Count} materials, {byShader.Count()} shaders");
            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Sprites

        private static string DiscoverSprites(string subfolder)
        {
            var sb = new StringBuilder();
            string[] searchFolders = { "Assets" };

            if (!string.IsNullOrEmpty(subfolder))
            {
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
                var names = spritePaths.OrderBy(p => p)
                    .Select(p => Path.GetFileNameWithoutExtension(p))
                    .ToArray();

                AppendColumns(sb, names, 4, 28, 200);
                sb.AppendLine();
                sb.AppendLine($"Total: {spritePaths.Length} sprites");
                sb.AppendLine($"Path: {searchFolders[0]}");
            }
            else
            {
                var groups = spritePaths
                    .GroupBy(p => Path.GetDirectoryName(p).Replace("\\", "/"))
                    .OrderByDescending(g => g.Count())
                    .ToList();

                string commonPrefix = FindCommonPrefix(groups.Select(g => g.Key).ToArray());
                if (!string.IsNullOrEmpty(commonPrefix))
                    sb.AppendLine($"Base: {commonPrefix}");

                foreach (var group in groups.Take(30))
                {
                    string displayPath = TrimPrefix(group.Key, commonPrefix);
                    var names = group.OrderBy(p => p).Select(p => Path.GetFileNameWithoutExtension(p)).ToArray();
                    sb.AppendLine($"  {displayPath}/ ({names.Length})");
                    AppendColumns(sb, names, 4, 28);
                }
                sb.AppendLine();
                sb.AppendLine($"Total: {spritePaths.Length} sprites in {groups.Count} folders");
            }

            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Prefabs

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
                var tmps = prefab.GetComponentsInChildren<TMP_Text>(true);
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
                    var tmps = prefab.GetComponentsInChildren<TMP_Text>(true);
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

        #endregion

        #region Scenes

        private static string DiscoverScenes()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Scenes:");
            sb.AppendLine();

            var sceneGuids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" });
            int uiSceneCount = 0;

            // UI scenes first
            var uiScenes = new List<string>();
            var otherScenes = new List<string>();

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
                        uiScenes.Add($"  {path}  [{string.Join(", ", indicators)}]");
                    }
                    else
                    {
                        otherScenes.Add($"  {path}");
                    }
                }
                catch
                {
                    otherScenes.Add($"  {path}");
                }
            }

            if (uiScenes.Count > 0)
            {
                sb.AppendLine($"## With UI ({uiScenes.Count})");
                foreach (var s in uiScenes)
                    sb.AppendLine(s);
                sb.AppendLine();
            }

            if (otherScenes.Count > 0)
            {
                sb.AppendLine($"## Other ({otherScenes.Count})");
                foreach (var s in otherScenes)
                    sb.AppendLine(s);
            }

            if (sceneGuids.Length == 0)
                sb.AppendLine("  (no scenes found)");

            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Fonts

        private static string DiscoverFonts()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Available Fonts:");
            sb.AppendLine();

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

            sb.AppendLine("## TextMeshPro Font Assets");
            var tmpGuids = AssetDatabase.FindAssets("t:TMP_FontAsset", new[] { "Assets" });
            foreach (var guid in tmpGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                sb.AppendLine($"  {path}");
            }
            var tmpPackageGuids = AssetDatabase.FindAssets("t:TMP_FontAsset", new[] { "Packages" });
            if (tmpPackageGuids.Length > 0)
                sb.AppendLine($"  + {tmpPackageGuids.Length} built-in TMP fonts (in Packages/)");
            if (tmpGuids.Length == 0 && tmpPackageGuids.Length == 0)
                sb.AppendLine("  (none)");

            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Models

        private static string DiscoverModels()
        {
            var sb = new StringBuilder();
            var modelGuids = AssetDatabase.FindAssets("t:Model", new[] { "Assets" });

            if (modelGuids.Length == 0)
            {
                sb.AppendLine("No model assets found.");
                return sb.ToString().TrimEnd();
            }

            sb.AppendLine($"=== Models ({modelGuids.Length}) ===");
            sb.AppendLine();

            // Group by folder
            var models = modelGuids
                .Select(g => AssetDatabase.GUIDToAssetPath(g))
                .OrderBy(p => p)
                .ToList();

            var byFolder = models
                .GroupBy(p => Path.GetDirectoryName(p).Replace("\\", "/"))
                .OrderBy(g => g.Key);

            foreach (var group in byFolder)
            {
                sb.AppendLine($"## {group.Key}/");
                foreach (var path in group)
                {
                    string name = Path.GetFileName(path);
                    var subAssets = AssetDatabase.LoadAllAssetsAtPath(path);

                    int meshCount = 0, matCount = 0, clipCount = 0;
                    var meshNames = new List<string>();
                    var matNames = new List<string>();
                    var clipNames = new List<string>();

                    foreach (var sub in subAssets)
                    {
                        if (sub == null) continue;
                        if (sub is Mesh m)
                        {
                            meshCount++;
                            if (meshNames.Count < 5) meshNames.Add(m.name);
                        }
                        else if (sub is Material mat)
                        {
                            matCount++;
                            if (matNames.Count < 5) matNames.Add($"{mat.name} ({mat.shader?.name ?? "?"})");
                        }
                        else if (sub is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                        {
                            clipCount++;
                            if (clipNames.Count < 5) clipNames.Add($"{clip.name} ({clip.length:F1}s)");
                        }
                    }

                    var parts = new List<string>();
                    if (meshCount > 0) parts.Add($"{meshCount} mesh");
                    if (matCount > 0) parts.Add($"{matCount} mat");
                    if (clipCount > 0) parts.Add($"{clipCount} clip");

                    sb.AppendLine($"  {name}  ({string.Join(", ", parts)})");

                    if (meshNames.Count > 0 && meshCount <= 5)
                        sb.AppendLine($"    Meshes: {string.Join(", ", meshNames)}");
                    if (matNames.Count > 0)
                    {
                        foreach (var mn in matNames)
                            sb.AppendLine($"    Mat: {mn}");
                        if (matCount > 5)
                            sb.AppendLine($"    ... +{matCount - 5} more materials");
                    }
                    if (clipNames.Count > 0)
                    {
                        foreach (var cn in clipNames)
                            sb.AppendLine($"    Clip: {cn}");
                        if (clipCount > 5)
                            sb.AppendLine($"    ... +{clipCount - 5} more clips");
                    }
                }
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Variants

        private static string DiscoverVariants()
        {
            var sb = new StringBuilder();
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });

            // Build base→variants map
            var variantMap = new Dictionary<string, List<string>>(); // basePath → [variantPaths]
            var allVariants = new HashSet<string>();

            foreach (var guid in prefabGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;

                if (PrefabUtility.GetPrefabAssetType(go) != PrefabAssetType.Variant)
                    continue;

                allVariants.Add(path);

                var baseObj = PrefabUtility.GetCorrespondingObjectFromSource(go);
                if (baseObj == null) continue;

                string basePath = AssetDatabase.GetAssetPath(baseObj);
                if (string.IsNullOrEmpty(basePath)) continue;

                if (!variantMap.TryGetValue(basePath, out var list))
                {
                    list = new List<string>();
                    variantMap[basePath] = list;
                }
                list.Add(path);
            }

            if (variantMap.Count == 0)
            {
                sb.AppendLine("No prefab variants found.");
                return sb.ToString().TrimEnd();
            }

            sb.AppendLine($"=== Prefab Variants ({allVariants.Count} variants from {variantMap.Count} bases) ===");
            sb.AppendLine();

            // Show trees: bases that are themselves variants form chains
            foreach (var kvp in variantMap.OrderBy(k => k.Key))
            {
                string basePath = kvp.Key;
                bool baseIsVariant = allVariants.Contains(basePath);
                string marker = baseIsVariant ? " (variant)" : "";

                sb.AppendLine($"  {basePath}{marker}");
                foreach (var variant in kvp.Value.OrderBy(v => v))
                {
                    // Check if this variant is also a base for other variants
                    bool hasChildren = variantMap.ContainsKey(variant);
                    string arrow = hasChildren ? "├─▶" : "└──";
                    sb.AppendLine($"    {arrow} {variant}");

                    // Show overrides summary
                    var variantGo = AssetDatabase.LoadAssetAtPath<GameObject>(variant);
                    if (variantGo != null)
                    {
                        var overrides = PrefabUtility.GetObjectOverrides(variantGo, false);
                        var addedComponents = PrefabUtility.GetAddedComponents(variantGo);
                        var removedComponents = PrefabUtility.GetRemovedComponents(variantGo);
                        var addedObjects = PrefabUtility.GetAddedGameObjects(variantGo);

                        var changes = new List<string>();
                        if (overrides.Count > 0) changes.Add($"{overrides.Count} override{(overrides.Count != 1 ? "s" : "")}");
                        if (addedComponents.Count > 0) changes.Add($"+{addedComponents.Count} comp");
                        if (removedComponents.Count > 0) changes.Add($"-{removedComponents.Count} comp");
                        if (addedObjects.Count > 0) changes.Add($"+{addedObjects.Count} obj");

                        if (changes.Count > 0)
                            sb.AppendLine($"         ({string.Join(", ", changes)})");
                    }
                }
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Helpers

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

            var prefix = first.Substring(0, prefixLen);
            int lastSlash = prefix.LastIndexOf('/');
            return lastSlash >= 0 ? prefix.Substring(0, lastSlash + 1) : "";
        }

        private static string TrimPrefix(string path, string prefix)
        {
            return !string.IsNullOrEmpty(prefix) && path.StartsWith(prefix)
                ? path.Substring(prefix.Length)
                : path;
        }

        private static void AppendColumns(StringBuilder sb, string[] names, int cols, int colWidth, int maxItems = 0)
        {
            int limit = maxItems > 0 ? Math.Min(names.Length, maxItems) : names.Length;
            for (int i = 0; i < limit; i += cols)
            {
                var batch = names.Skip(i).Take(cols).Select(n => n.PadRight(colWidth));
                sb.AppendLine($"    {string.Join("", batch)}");
            }
            if (maxItems > 0 && names.Length > maxItems)
                sb.AppendLine($"    ... and {names.Length - maxItems} more");
        }

        #endregion
    }
}
