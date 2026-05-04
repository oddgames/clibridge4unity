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

        /// <summary>Top `max` asset paths whose filename resembles the failed `path`'s filename.
        /// Two-pass: AssetDatabase.FindAssets fast path, then full enumeration with fuzzy
        /// scoring (handles typos / acronyms that Unity Search tokenizer misses).</summary>
        public static List<string> SuggestAsset(string path, int max = 5)
        {
            if (string.IsNullOrWhiteSpace(path)) return new List<string>();
            string stem = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(stem)) return new List<string>();
            string needle = stem.ToLowerInvariant();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<string>();

            // Pass 1: Unity's tokenizer-based search (cheap, hits exact/prefix names).
            foreach (var g in AssetDatabase.FindAssets(stem))
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                if (string.IsNullOrEmpty(p) || p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(p)) continue;
                results.Add(p);
                if (results.Count >= max) return results;
            }

            // Pass 2: enumerate all asset paths and fuzzy-rank by filename. Catches
            // typos / partial / acronym queries Unity Search drops. Capped for big projects.
            const int enumerationCap = 30000;
            var scored = new List<(int score, string path)>();
            var allPaths = AssetDatabase.GetAllAssetPaths();
            int budget = Math.Min(allPaths.Length, enumerationCap);
            for (int i = 0; i < budget; i++)
            {
                string p = allPaths[i];
                if (string.IsNullOrEmpty(p)) continue;
                if (!p.StartsWith("Assets/", StringComparison.Ordinal)
                 && !p.StartsWith("Packages/", StringComparison.Ordinal)) continue;
                if (p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Contains(p)) continue;

                string fileStem = Path.GetFileNameWithoutExtension(p);
                int s = NameSimilarity(fileStem, needle);
                if (s >= 30) scored.Add((s, p));
            }
            foreach (var (_, p) in scored.OrderByDescending(x => x.score).Take(max - results.Count))
                results.Add(p);
            return results;
        }

        /// <summary>Fuzzy similarity score 0..100. Wraps shared <see cref="FuzzyMatch.Score"/>.
        /// `needleLower` MUST be lowercased by caller.</summary>
        public static int NameSimilarity(string candidate, string needleLower)
            => FuzzyMatch.Score(candidate, needleLower);

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
