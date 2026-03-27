using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;

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

        [BridgeCommand("ASSET_COPY", "Copy assets (new GUIDs, preserves content)",
            Category = "Asset",
            Usage = "ASSET_COPY Assets/Source.prefab Assets/Dest.prefab        (single)\n" +
                    "  ASSET_COPY Assets/A.prefab Assets/B.mat Assets/Dest/      (multi → folder)",
            RequiresMainThread = true)]
        public static string Copy(string data)
        {
            var args = ParseArgs(data, 2);
            if (args == null)
                return Response.Error("Usage: ASSET_COPY <source...> <destination>");

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
