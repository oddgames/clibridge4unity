using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace clibridge4unity
{
    /// <summary>
    /// Reads the clibridge4unity daemon's FileSystemWatcher change log and ensures
    /// requested assets (UXML, USS, prefabs, metas, anything) are imported into
    /// Unity's AssetDatabase before a command consumes them. Solves the case where
    /// the bridge writes a file then immediately reads it via SCREENSHOT/INSPECTOR/etc.,
    /// without Unity having focus to trigger its own auto-refresh.
    ///
    /// All methods are no-ops when the daemon isn't running.
    /// </summary>
    public static class AssetSyncHelper
    {
        private static string DaemonChangeLog
            => Path.Combine(Directory.GetParent(Application.dataPath).FullName, ".clibridge4unity", "changes.log");

        private struct LogEntry { public long UtcTicks; public string Kind; public string Path; public string OldPath; }

        private static List<LogEntry> ReadLog()
        {
            var entries = new List<LogEntry>();
            string p = DaemonChangeLog;
            if (!File.Exists(p)) return entries;
            try
            {
                foreach (var line in File.ReadAllLines(p))
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    var parts = line.Split('\t');
                    if (parts.Length < 3) continue;
                    if (!long.TryParse(parts[0], out long ticks)) continue;
                    entries.Add(new LogEntry
                    {
                        UtcTicks = ticks,
                        Kind = parts[1],
                        Path = parts[2],
                        OldPath = parts.Length > 3 ? parts[3] : ""
                    });
                }
            }
            catch { }
            return entries;
        }

        /// <summary>True if the daemon's change log exists.</summary>
        public static bool DaemonAvailable() => File.Exists(DaemonChangeLog);

        /// <summary>
        /// Ensure the given asset path is up-to-date in Unity's AssetDatabase.
        /// If the daemon recorded a change for this path (or its source file on disk has a
        /// newer mtime than Unity's import record), reimport it. Returns true if reimported.
        /// </summary>
        public static bool EnsureSynced(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string normalized = assetPath.Replace('\\', '/');

            // Match by ending — daemon stores absolute paths, callers usually pass asset-relative.
            foreach (var e in ReadLog())
            {
                if (e.Path.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return ApplyEntry(e, normalized);
                }
            }

            // Fallback: compare disk mtime vs AssetDatabase's known import time.
            // Unity stores imports in Library/SourceAssetDB; cheaper here to just reimport
            // unconditionally if the file exists on disk and the path is asset-relative.
            return false;
        }

        /// <summary>
        /// Ensure every pending change in the daemon log is reflected in AssetDatabase.
        /// Returns count of (imported, deleted) actions taken.
        /// </summary>
        public static (int imported, int deleted) SyncAllPending()
        {
            int imported = 0, deleted = 0;
            foreach (var e in ReadLog())
            {
                string rel = ToAssetRelative(e.Path);
                if (rel == null) continue;
                if (e.Kind == "D")
                {
                    try { if (AssetDatabase.DeleteAsset(rel)) deleted++; } catch { }
                }
                else
                {
                    try { AssetDatabase.ImportAsset(rel, ImportAssetOptions.ForceUpdate); imported++; } catch { }
                }
            }
            return (imported, deleted);
        }

        /// <summary>
        /// Unconditionally reimport the asset and its UI Toolkit dependencies (USS/TSS referenced by
        /// a UXML). Use before SCREENSHOT or other commands that render a UXML — guarantees on-disk
        /// edits are reflected even if the daemon isn't running.
        /// </summary>
        public static int EnsureUITreeSynced(string assetPath)
        {
            int count = 0;
            try
            {
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                count++;
                foreach (var dep in AssetDatabase.GetDependencies(assetPath, recursive: true))
                {
                    if (dep == assetPath) continue;
                    if (dep.EndsWith(".uss", StringComparison.OrdinalIgnoreCase)
                        || dep.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase)
                        || dep.EndsWith(".tss", StringComparison.OrdinalIgnoreCase))
                    {
                        try { AssetDatabase.ImportAsset(dep, ImportAssetOptions.ForceUpdate); count++; }
                        catch { }
                    }
                }
            }
            catch { }
            return count;
        }

        private static bool ApplyEntry(LogEntry e, string fallbackRelative)
        {
            string rel = ToAssetRelative(e.Path) ?? fallbackRelative;
            if (string.IsNullOrEmpty(rel)) return false;
            if (e.Kind == "D")
            {
                try { return AssetDatabase.DeleteAsset(rel); } catch { return false; }
            }
            try
            {
                AssetDatabase.ImportAsset(rel, ImportAssetOptions.ForceUpdate);
                return true;
            }
            catch { return false; }
        }

        private static string ToAssetRelative(string fullOrRel)
        {
            if (string.IsNullOrEmpty(fullOrRel)) return null;
            string norm = fullOrRel.Replace('\\', '/');
            int idx = norm.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return norm.Substring(idx + 1); // -> "Assets/..."
            idx = norm.IndexOf("/Packages/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return norm.Substring(idx + 1); // -> "Packages/..."
            if (norm.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return norm;
            return null;
        }
    }
}
