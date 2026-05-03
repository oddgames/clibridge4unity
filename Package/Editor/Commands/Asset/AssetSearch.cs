using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Search;
using UnityEngine;

namespace clibridge4unity
{
    /// <summary>
    /// Asset search using Unity's built-in Search API.
    /// Supports searching prefabs by component, materials by shader, and more.
    /// </summary>
    public static class AssetSearch
    {
        /// <summary>
        /// Search assets using Unity Search syntax.
        /// Examples:
        ///   "t:prefab"                    - All prefabs
        ///   "t:prefab ref:PlayerController" - Prefabs with PlayerController component
        ///   "t:material shader:Standard"  - Materials using Standard shader
        ///   "t:texture label:UI"          - Textures with UI label
        ///   "*.fbx"                        - All FBX files
        /// </summary>
        [BridgeCommand("ASSET_SEARCH", "Search for assets using Unity Search syntax",
            Category = "Asset",
            Usage = "ASSET_SEARCH t:prefab",
            RequiresMainThread = true,
            RelatedCommands = new[] { "ASSET_DISCOVER", "ASSET_LABEL", "ASSET_MOVE" })]
        public static string Search(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return "Error: Empty search query";

            string query = data.Trim();
            int maxResults = 50;

            try
            {
                var results = new List<SearchItem>();
                var completed = false;

                // Use SearchService.Request with callback
                SearchService.Request(query, (context, items) =>
                {
                    results.AddRange(items.Take(maxResults));
                    completed = true;
                }, SearchFlags.Synchronous);

                // Wait for synchronous completion
                if (!completed)
                {
                    return "Error: Search did not complete";
                }

                if (results.Count == 0)
                    return $"No results for: {query}";

                // Dedupe by asset path — Unity Search can return the same asset from multiple
                // providers (asset, scene, animation, etc.) which spam the result list.
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var rows = new List<(string path, string extra)>();
                int dupesSkipped = 0;
                foreach (var item in results)
                {
                    var path = GetItemPath(item);
                    var desc = item.GetDescription(item.context);

                    string key = !string.IsNullOrEmpty(path) ? path : (item.label ?? item.id);
                    if (string.IsNullOrEmpty(key)) continue;
                    if (!seen.Add(key)) { dupesSkipped++; continue; }

                    // Pick the most informative single line. desc usually = "path (size)".
                    // If desc contains path, use desc as-is. Else fall back to path or label.
                    string display;
                    if (!string.IsNullOrEmpty(desc) && !string.IsNullOrEmpty(path)
                        && desc.IndexOf(path, StringComparison.Ordinal) >= 0)
                        display = desc;
                    else if (!string.IsNullOrEmpty(path))
                        display = path;
                    else
                        display = item.label ?? item.id;

                    rows.Add((key, display));
                    if (rows.Count >= maxResults) break;
                }

                var sb = new StringBuilder();
                string countSuffix = dupesSkipped > 0 ? $" ({dupesSkipped} duplicate(s) merged)" : "";
                sb.AppendLine($"Found {rows.Count} results for: {query}{countSuffix}");
                sb.AppendLine();
                foreach (var row in rows)
                    sb.AppendLine($"  {row.extra}");

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// Find all prefabs containing a specific component type.
        /// </summary>
        public static string FindPrefabsWithComponent(string componentName, int maxResults = 50)
        {
            // Unity Search syntax for finding prefabs with a component reference
            return Search($"t:prefab ref:{componentName}");
        }

        /// <summary>
        /// Find all materials using a specific shader.
        /// </summary>
        public static string FindMaterialsWithShader(string shaderName, int maxResults = 50)
        {
            // Search for materials, then filter by shader
            try
            {
                var guids = AssetDatabase.FindAssets("t:Material");
                var results = new List<string>();

                foreach (var guid in guids)
                {
                    if (results.Count >= maxResults) break;

                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(path);

                    if (mat != null && mat.shader != null)
                    {
                        if (mat.shader.name.IndexOf(shaderName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            results.Add($"{path} (Shader: {mat.shader.name})");
                        }
                    }
                }

                if (results.Count == 0)
                    return $"No materials found with shader containing: {shaderName}";

                var sb = new StringBuilder();
                sb.AppendLine($"Found {results.Count} materials with shader '{shaderName}':");
                sb.AppendLine();
                foreach (var r in results)
                    sb.AppendLine($"  {r}");

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// Find all assets with a specific label.
        /// </summary>
        public static string FindAssetsWithLabel(string label, int maxResults = 50)
        {
            return Search($"l:{label}");
        }

        /// <summary>
        /// Find all scripts that inherit from a specific base type.
        /// </summary>
        public static string FindScriptsInheriting(string baseTypeName, int maxResults = 50)
        {
            try
            {
                var results = new List<string>();
                var guids = AssetDatabase.FindAssets("t:MonoScript");

                foreach (var guid in guids)
                {
                    if (results.Count >= maxResults) break;

                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);

                    if (script != null)
                    {
                        var type = script.GetClass();
                        if (type != null && InheritsFrom(type, baseTypeName))
                        {
                            var baseType = type.BaseType;
                            results.Add($"{path} ({type.Name} : {baseType?.Name ?? "?"})");
                        }
                    }
                }

                if (results.Count == 0)
                    return $"No scripts found inheriting from: {baseTypeName}";

                var sb = new StringBuilder();
                sb.AppendLine($"Found {results.Count} scripts inheriting from '{baseTypeName}':");
                sb.AppendLine();
                foreach (var r in results)
                    sb.AppendLine($"  {r}");

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// Find all assets of a specific type in a folder.
        /// </summary>
        public static string FindAssetsOfType(string typeName, string folder = "Assets", int maxResults = 50)
        {
            try
            {
                var guids = AssetDatabase.FindAssets($"t:{typeName}", new[] { folder });

                if (guids.Length == 0)
                    return $"No {typeName} assets found in {folder}";

                var sb = new StringBuilder();
                sb.AppendLine($"Found {guids.Length} {typeName} assets in {folder}:");
                sb.AppendLine();

                foreach (var guid in guids.Take(maxResults))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    sb.AppendLine($"  {path}");
                }

                if (guids.Length > maxResults)
                    sb.AppendLine($"  ... and {guids.Length - maxResults} more");

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// Get asset dependencies (what this asset references).
        /// </summary>
        public static string GetDependencies(string assetPath, bool recursive = false)
        {
            try
            {
                if (!AssetDatabase.AssetPathExists(assetPath))
                    return $"Error: Asset not found: {assetPath}";

                var deps = AssetDatabase.GetDependencies(assetPath, recursive);

                // Filter out the asset itself
                deps = deps.Where(d => d != assetPath).ToArray();

                if (deps.Length == 0)
                    return $"No dependencies found for: {assetPath}";

                var sb = new StringBuilder();
                sb.AppendLine($"Dependencies for {assetPath}{(recursive ? " (recursive)" : "")}:");
                sb.AppendLine();

                // Group by type
                var grouped = deps.GroupBy(d => System.IO.Path.GetExtension(d).ToLower());
                foreach (var group in grouped.OrderBy(g => g.Key))
                {
                    sb.AppendLine($"  {group.Key} ({group.Count()}):");
                    foreach (var dep in group.Take(20))
                        sb.AppendLine($"    {dep}");
                    if (group.Count() > 20)
                        sb.AppendLine($"    ... and {group.Count() - 20} more");
                }

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// Find what references a specific asset (reverse dependencies).
        /// </summary>
        public static string FindReferences(string assetPath, int maxResults = 50)
        {
            try
            {
                if (!AssetDatabase.AssetPathExists(assetPath))
                    return $"Error: Asset not found: {assetPath}";

                var results = new List<string>();
                var allAssets = AssetDatabase.GetAllAssetPaths();

                foreach (var path in allAssets)
                {
                    if (results.Count >= maxResults) break;
                    if (path == assetPath) continue;
                    if (!path.StartsWith("Assets/")) continue; // Skip packages

                    var deps = AssetDatabase.GetDependencies(path, false);
                    if (deps.Contains(assetPath))
                        results.Add(path);
                }

                if (results.Count == 0)
                    return $"No references found to: {assetPath}";

                var sb = new StringBuilder();
                sb.AppendLine($"Found {results.Count} references to {assetPath}:");
                sb.AppendLine();
                foreach (var r in results)
                    sb.AppendLine($"  {r}");

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.GetType().Name}: {ex.Message}";
            }
        }

        #region Helpers

        private static string GetItemPath(SearchItem item)
        {
            // Try to get the asset path from the item data
            if (item.data is UnityEngine.Object obj)
            {
                var path = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(path))
                    return path;
            }

            // Try to get it from the id (sometimes it's the path directly)
            if (!string.IsNullOrEmpty(item.id))
            {
                if (item.id.StartsWith("Assets/") || item.id.StartsWith("Packages/"))
                    return item.id;

                // Try to parse GlobalObjectId format
                if (item.id.StartsWith("GlobalObjectId"))
                {
                    if (GlobalObjectId.TryParse(item.id, out var goid))
                    {
                        var guidPath = AssetDatabase.GUIDToAssetPath(goid.assetGUID);
                        if (!string.IsNullOrEmpty(guidPath))
                            return guidPath;
                    }
                }
            }

            // Try to convert to object and get path
            var itemObj = item.ToObject();
            if (itemObj != null)
            {
                var path = AssetDatabase.GetAssetPath(itemObj);
                if (!string.IsNullOrEmpty(path))
                    return path;
            }

            return null;
        }

        private static bool InheritsFrom(Type type, string baseTypeName)
        {
            var current = type.BaseType;
            while (current != null && current != typeof(object))
            {
                if (current.Name.Equals(baseTypeName, StringComparison.OrdinalIgnoreCase) ||
                    current.FullName?.Equals(baseTypeName, StringComparison.OrdinalIgnoreCase) == true)
                    return true;
                current = current.BaseType;
            }

            // Check interfaces
            return type.GetInterfaces().Any(i =>
                i.Name.Equals(baseTypeName, StringComparison.OrdinalIgnoreCase) ||
                i.FullName?.Equals(baseTypeName, StringComparison.OrdinalIgnoreCase) == true);
        }

        #endregion
    }
}
