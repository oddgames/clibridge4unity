using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace clibridge4unity
{
    /// <summary>
    /// Helpers for "did you mean…?" suggestions on path lookup failures.
    /// Scene GameObjects → name substring / prefix score across the active scene.
    /// Asset paths → AssetDatabase filename search.
    /// Commands call <see cref="Append"/> in their not-found error branches so the
    /// response tells the caller what was nearby instead of just "not found".
    /// </summary>
    public static class PathResolver
    {
        /// <summary>
        /// Append "Did you mean:" + top matches to <paramref name="sb"/>. No-op if no
        /// suggestions. `kind` picks between scene / asset scoring.
        /// </summary>
        public static void Append(StringBuilder sb, string missing, SuggestKind kind, int max = 5)
        {
            List<string> suggestions = kind switch
            {
                SuggestKind.SceneGameObject => SuggestSceneObject(missing, max),
                SuggestKind.Asset => SuggestAsset(missing, max),
                SuggestKind.Any => SuggestSceneObject(missing, max)
                                    .Concat(SuggestAsset(missing, max))
                                    .Take(max).ToList(),
                _ => new List<string>()
            };
            if (suggestions.Count == 0) return;

            sb.AppendLine();
            sb.AppendLine("Did you mean:");
            foreach (var s in suggestions) sb.AppendLine($"  {s}");
        }

        /// <summary>Same as <see cref="Append"/> but returns a one-off string — for Response.Error callers.</summary>
        public static string FormatSuggestions(string missing, SuggestKind kind, int max = 5)
        {
            var sb = new StringBuilder();
            Append(sb, missing, kind, max);
            return sb.ToString();
        }

        public enum SuggestKind { SceneGameObject, Asset, Any }

        /// <summary>Top `max` scene GameObject paths whose name is close to the last segment of `query`.</summary>
        public static List<string> SuggestSceneObject(string query, int max = 5)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<string>();
            string needle = LastSegment(query).ToLowerInvariant();
            if (needle.Length == 0) return new List<string>();

            var all = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var scored = new List<(int score, string path)>();
            foreach (var go in all)
            {
                int s = NameSimilarity(go.name, needle);
                if (s > 0) scored.Add((s, GetScenePath(go)));
            }
            return scored.OrderByDescending(x => x.score).Take(max).Select(x => x.path).ToList();
        }

        /// <summary>Top `max` asset paths whose filename resembles the failed `path`'s filename.</summary>
        public static List<string> SuggestAsset(string path, int max = 5)
        {
            if (string.IsNullOrWhiteSpace(path)) return new List<string>();
            string stem = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(stem)) return new List<string>();

            // AssetDatabase.FindAssets does fuzzy filename matching.
            var guids = AssetDatabase.FindAssets(stem);
            var results = new List<string>();
            foreach (var g in guids)
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                if (string.IsNullOrEmpty(p) || p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                results.Add(p);
                if (results.Count >= max) break;
            }
            return results;
        }

        static int NameSimilarity(string candidate, string needleLower)
        {
            string c = candidate.ToLowerInvariant();
            if (c == needleLower) return 100;
            if (c.StartsWith(needleLower, StringComparison.Ordinal)) return 80;
            if (c.Contains(needleLower)) return 60;

            // Partial token overlap (split on separators — handles "Main_Camera" vs "MainCamera").
            int score = 0;
            foreach (var tok in needleLower.Split(new[] { ' ', '/', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries))
                if (tok.Length >= 3 && c.Contains(tok)) score += 10;
            return score;
        }

        static string LastSegment(string path)
        {
            int i = path.LastIndexOfAny(new[] { '/', '\\' });
            return i >= 0 ? path.Substring(i + 1) : path;
        }

        static string GetScenePath(GameObject go)
        {
            var parts = new List<string>();
            var t = go.transform;
            while (t != null) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
