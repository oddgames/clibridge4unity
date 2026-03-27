using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace clibridge4unity
{
    public static class AssetManagement
    {
        [BridgeCommand("ASSET_MOVE", "Move/rename assets (preserves GUID references)",
            Category = "Asset",
            Usage = "ASSET_MOVE Assets/Old.prefab Assets/New.prefab           (single)\n" +
                    "  ASSET_MOVE Assets/A.prefab Assets/B.mat Assets/Dest/     (multi → folder)\n" +
                    "  ASSET_MOVE Assets/OldFolder Assets/NewFolder",
            RequiresMainThread = true)]
        public static string Move(string data)
        {
            var args = ParseArgs(data, 2);
            if (args == null)
                return Response.Error("Usage: ASSET_MOVE <source...> <destination>");

            return BatchMoveOrCopy(args, isCopy: false);
        }

        [BridgeCommand("ASSET_COPY", "Copy assets, extract GameObjects from prefabs/scenes as new prefabs",
            Category = "Asset",
            Usage = "ASSET_COPY Assets/A.prefab Assets/B.prefab                        (copy asset)\n" +
                    "  ASSET_COPY Assets/A.prefab Assets/B.mat Assets/Dest/               (batch → folder)\n" +
                    "  ASSET_COPY Assets/Big.prefab/ChildName Assets/Child.prefab          (extract from prefab)\n" +
                    "  ASSET_COPY scene/Player Assets/Player.prefab                        (scene GO → prefab)\n" +
                    "  ASSET_COPY scene/Player scene/PlayerCopy                            (clone in scene)\n" +
                    "  ASSET_COPY Assets/Level.unity/Enemy Assets/Enemy.prefab             (from unopened scene)\n" +
                    "  ASSET_COPY Assets/A.prefab/Child scene/Parent                       (prefab child → scene)",
            RequiresMainThread = true)]
        public static string Copy(string data)
        {
            var args = ParseArgs(data, 2);
            if (args == null)
                return Response.Error("Usage: ASSET_COPY <source> <destination>");

            string src = args[0];
            string dst = args[args.Length - 1];

            // Detect GameObject copy (source contains / after .prefab, .unity, or starts with scene/)
            bool srcIsGo = IsGameObjectPath(src);
            bool dstIsGo = dst.StartsWith("scene/", StringComparison.OrdinalIgnoreCase);

            if (srcIsGo || dstIsGo)
                return GameObjectCopy(src, dst);

            // Standard asset copy
            return BatchMoveOrCopy(args, isCopy: true);
        }

        [BridgeCommand("ASSET_DELETE", "Delete an asset or folder",
            Category = "Asset",
            Usage = "ASSET_DELETE Assets/Path/To/Asset.prefab\n" +
                    "  ASSET_DELETE Assets/Path1 Assets/Path2   (batch)",
            RequiresMainThread = true)]
        public static string Delete(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return Response.Error("Usage: ASSET_DELETE <path> [path2 ...]");

            var paths = data.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var failed = new StringBuilder();
            int deleted = 0;

            foreach (var path in paths)
            {
                if (!AssetExists(path))
                {
                    failed.AppendLine($"  Not found: {path}");
                    continue;
                }

                if (AssetDatabase.DeleteAsset(path))
                    deleted++;
                else
                    failed.AppendLine($"  Failed: {path}");
            }

            if (failed.Length > 0)
                return Response.Error($"Deleted {deleted}/{paths.Length}:\n{failed.ToString().TrimEnd()}");

            return Response.Success($"Deleted {deleted} asset(s)");
        }

        [BridgeCommand("ASSET_MKDIR", "Create folders in the asset database",
            Category = "Asset",
            Usage = "ASSET_MKDIR Assets/Art/Textures/UI\n" +
                    "  ASSET_MKDIR Assets/Folder1 Assets/Folder2   (batch)",
            RequiresMainThread = true)]
        public static string Mkdir(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return Response.Error("Usage: ASSET_MKDIR <path> [path2 ...]");

            var paths = data.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            int created = 0;

            foreach (var path in paths)
            {
                if (AssetDatabase.IsValidFolder(path))
                {
                    sb.AppendLine($"  Already exists: {path}");
                    created++;
                    continue;
                }

                string result = CreateFolderRecursive(path);
                if (result == null)
                {
                    sb.AppendLine($"  Created: {path}");
                    created++;
                }
                else
                    sb.AppendLine($"  Failed: {result}");
            }

            if (created == paths.Length)
                return Response.Success(paths.Length == 1
                    ? sb.ToString().Trim()
                    : $"{created} folder(s):\n{sb.ToString().TrimEnd()}");

            return Response.Error($"{created}/{paths.Length} folders:\n{sb.ToString().TrimEnd()}");
        }

        [BridgeCommand("ASSET_LABEL", "Get or set asset labels",
            Category = "Asset",
            Usage = "ASSET_LABEL Assets/MyAsset.prefab                   (get)\n" +
                    "  ASSET_LABEL Assets/MyAsset.prefab +tag1 +tag2 -old  (add/remove)",
            RequiresMainThread = true)]
        public static string Label(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return Response.Error("Usage: ASSET_LABEL <path> [+add -remove ...]");

            var parts = data.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string path = parts[0];

            var obj = AssetDatabase.LoadMainAssetAtPath(path);
            if (obj == null)
                return Response.Error($"Asset not found: {path}");

            // Get-only mode
            if (parts.Length == 1)
            {
                var labels = AssetDatabase.GetLabels(obj);
                if (labels.Length == 0)
                    return Response.Success($"{path}: (no labels)");
                return Response.Success($"{path}: {string.Join(", ", labels)}");
            }

            // Modify labels
            var current = AssetDatabase.GetLabels(obj).ToList();
            foreach (var op in parts.Skip(1))
            {
                if (op.StartsWith("+"))
                {
                    string label = op.Substring(1);
                    if (!current.Contains(label, StringComparer.OrdinalIgnoreCase))
                        current.Add(label);
                }
                else if (op.StartsWith("-"))
                {
                    string label = op.Substring(1);
                    current.RemoveAll(l => l.Equals(label, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    // No prefix = add
                    if (!current.Contains(op, StringComparer.OrdinalIgnoreCase))
                        current.Add(op);
                }
            }

            AssetDatabase.SetLabels(obj, current.ToArray());
            return Response.Success($"{path}: {string.Join(", ", current)}");
        }

        #region GameObject Copy

        static bool IsGameObjectPath(string path)
        {
            // scene/Player, Assets/X.prefab/Child, Assets/X.unity/Child
            if (path.StartsWith("scene/", StringComparison.OrdinalIgnoreCase))
                return true;

            // Check for child path after .prefab or .unity
            int prefabIdx = path.IndexOf(".prefab/", StringComparison.OrdinalIgnoreCase);
            if (prefabIdx >= 0) return true;
            int unityIdx = path.IndexOf(".unity/", StringComparison.OrdinalIgnoreCase);
            if (unityIdx >= 0) return true;

            return false;
        }

        /// <summary>
        /// Parses a source like "Assets/X.prefab/ChildName" into (assetPath, childName)
        /// or "scene/Player" into (null, "Player")
        /// </summary>
        static (string assetPath, string childPath) ParseGoPath(string path)
        {
            if (path.StartsWith("scene/", StringComparison.OrdinalIgnoreCase))
                return (null, path.Substring("scene/".Length));

            int idx = path.IndexOf(".prefab/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                return (path.Substring(0, idx + ".prefab".Length), path.Substring(idx + ".prefab/".Length));

            idx = path.IndexOf(".unity/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                return (path.Substring(0, idx + ".unity".Length), path.Substring(idx + ".unity/".Length));

            return (null, path);
        }

        static string GameObjectCopy(string src, string dst)
        {
            var (srcAsset, srcChild) = ParseGoPath(src);
            bool dstIsScene = dst.StartsWith("scene/", StringComparison.OrdinalIgnoreCase);
            string dstScenePath = dstIsScene ? dst.Substring("scene/".Length) : null;

            // Resolve source GameObject
            GameObject srcGo = null;
            GameObject tempInstance = null;
            UnityEngine.SceneManagement.Scene? tempScene = null;

            try
            {
                if (srcAsset == null)
                {
                    // Source is in the open scene
                    srcGo = GameObject.Find(srcChild);
                    if (srcGo == null)
                        return Response.Error($"GameObject not found in scene: {srcChild}");
                }
                else if (srcAsset.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(srcAsset);
                    if (prefab == null)
                        return Response.Error($"Prefab not found: {srcAsset}");

                    tempInstance = UnityEngine.Object.Instantiate(prefab);
                    tempInstance.hideFlags = HideFlags.HideAndDontSave;

                    if (string.IsNullOrEmpty(srcChild))
                    {
                        srcGo = tempInstance;
                    }
                    else
                    {
                        var child = FindChildRecursive(tempInstance.transform, srcChild);
                        if (child == null)
                            return Response.Error($"Child '{srcChild}' not found in {srcAsset}\n{ListChildren(tempInstance.transform)}");
                        srcGo = child.gameObject;
                    }
                }
                else if (srcAsset.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    if (!AssetDatabase.AssetPathExists(srcAsset))
                        return Response.Error($"Scene not found: {srcAsset}");

                    var scene = EditorSceneManager.OpenScene(srcAsset, OpenSceneMode.Additive);
                    tempScene = scene;

                    foreach (var root in scene.GetRootGameObjects())
                    {
                        if (root.name.Equals(srcChild, StringComparison.OrdinalIgnoreCase))
                        {
                            srcGo = root;
                            break;
                        }
                        var found = FindChildRecursive(root.transform, srcChild);
                        if (found != null) { srcGo = found.gameObject; break; }
                    }

                    if (srcGo == null)
                    {
                        var roots = string.Join(", ", scene.GetRootGameObjects().Select(r => r.name));
                        return Response.Error($"'{srcChild}' not found in {srcAsset}\nRoots: {roots}");
                    }
                }

                // Clone source
                var clone = UnityEngine.Object.Instantiate(srcGo);
                clone.name = srcGo.name;
                clone.hideFlags = HideFlags.HideAndDontSave;
                clone.transform.SetParent(null);

                // Move to active scene (in case source was from additive scene)
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clone,
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene());

                try
                {
                    if (dstIsScene)
                    {
                        // Destination is in the open scene
                        clone.hideFlags = HideFlags.None;
                        clone.name = dstScenePath.Contains("/")
                            ? dstScenePath.Substring(dstScenePath.LastIndexOf('/') + 1)
                            : dstScenePath;

                        // Parent under a scene object if path has a parent
                        if (dstScenePath.Contains("/"))
                        {
                            string parentPath = dstScenePath.Substring(0, dstScenePath.LastIndexOf('/'));
                            var parent = GameObject.Find(parentPath);
                            if (parent != null)
                                clone.transform.SetParent(parent.transform, false);
                        }

                        Undo.RegisterCreatedObjectUndo(clone, $"ASSET_COPY {src} → scene");

                        return Response.SuccessWithData(new
                        {
                            message = $"Copied to scene: {clone.name}",
                            source = src,
                            destination = dst,
                            childCount = clone.transform.childCount
                        });
                    }
                    else
                    {
                        // Destination is a prefab asset
                        if (!dst.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                            dst += ".prefab";

                        EnsureParentDirectory(dst);
                        var saved = PrefabUtility.SaveAsPrefabAsset(clone, dst);
                        if (saved == null)
                            return Response.Error($"Failed to save prefab: {dst}");

                        return Response.SuccessWithData(new
                        {
                            message = $"Copied: {src} → {dst}",
                            source = src,
                            path = dst,
                            name = saved.name,
                            childCount = saved.transform.childCount,
                            components = saved.GetComponents<Component>()
                                .Where(c => c != null).Select(c => c.GetType().Name).ToArray()
                        });
                    }
                }
                finally
                {
                    // Clean up clone if it was saved as prefab (not kept in scene)
                    if (!dstIsScene && clone != null)
                        UnityEngine.Object.DestroyImmediate(clone);
                }
            }
            finally
            {
                if (tempInstance != null) UnityEngine.Object.DestroyImmediate(tempInstance);
                if (tempScene.HasValue) EditorSceneManager.CloseScene(tempScene.Value, true);
            }
        }

        static Transform FindChildRecursive(Transform parent, string name)
        {
            // Try path-based lookup first (e.g. "Canvas/Panel")
            if (name.Contains("/"))
            {
                var found = parent.Find(name);
                if (found != null) return found;
            }

            foreach (Transform child in parent)
            {
                if (child.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return child;
                var result = FindChildRecursive(child, name);
                if (result != null) return result;
            }
            return null;
        }

        static string ListChildren(Transform parent, int maxDepth = 2, int depth = 0)
        {
            var sb = new StringBuilder();
            string indent = new string(' ', depth * 2 + 2);
            foreach (Transform child in parent)
            {
                sb.AppendLine($"{indent}{child.name}");
                if (depth < maxDepth)
                    sb.Append(ListChildren(child, maxDepth, depth + 1));
            }
            return sb.ToString();
        }

        #endregion

        #region Helpers

        static string BatchMoveOrCopy(string[] args, bool isCopy)
        {
            string op = isCopy ? "Copy" : "Move";
            string dst = args[args.Length - 1];
            string[] sources = args.Take(args.Length - 1).ToArray();

            // Multi-source: destination must be a folder
            bool dstIsFolder = dst.EndsWith("/") || AssetDatabase.IsValidFolder(dst);
            if (sources.Length > 1 && !dstIsFolder)
                return Response.Error($"Multiple sources require a folder destination (end with /): {dst}");

            // Ensure destination folder exists for multi-source or folder target
            if (dstIsFolder)
            {
                dst = dst.TrimEnd('/');
                CreateFolderRecursive(dst);
            }

            var sb = new StringBuilder();
            int ok = 0;

            foreach (var src in sources)
            {
                if (!AssetExists(src))
                {
                    sb.AppendLine($"  Not found: {src}");
                    continue;
                }

                string target = dstIsFolder
                    ? dst + "/" + Path.GetFileName(src)
                    : dst;

                EnsureParentDirectory(target);

                string err;
                if (isCopy)
                    err = AssetDatabase.CopyAsset(src, target) ? null : $"Failed to copy {src}";
                else
                    err = AssetDatabase.MoveAsset(src, target);

                if (string.IsNullOrEmpty(err))
                {
                    sb.AppendLine($"  {src} → {target}");
                    ok++;
                }
                else
                    sb.AppendLine($"  Error: {err}");
            }

            string verb = isCopy ? "Copied" : "Moved";
            if (ok == sources.Length)
                return Response.Success(sources.Length == 1
                    ? $"{verb}: {sb.ToString().Trim()}"
                    : $"{verb} {ok} asset(s):\n{sb.ToString().TrimEnd()}");

            return Response.Error($"{verb} {ok}/{sources.Length}:\n{sb.ToString().TrimEnd()}");
        }

        static string[] ParseArgs(string data, int expected)
        {
            if (string.IsNullOrWhiteSpace(data))
                return null;

            var parts = data.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= expected ? parts : null;
        }

        static bool AssetExists(string path)
        {
            return AssetDatabase.AssetPathExists(path) || AssetDatabase.IsValidFolder(path);
        }

        static string CreateFolderRecursive(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return null;

            var parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    string guid = AssetDatabase.CreateFolder(current, parts[i]);
                    if (string.IsNullOrEmpty(guid))
                        return $"Failed to create: {next}";
                }
                current = next;
            }
            return null;
        }

        static void EnsureParentDirectory(string assetPath)
        {
            string dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir))
                CreateFolderRecursive(dir);
        }

        #endregion
    }
}
