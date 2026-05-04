using System;
using System.Collections.Generic;
using System.Text;

namespace clibridge4unity
{
    /// <summary>
    /// Pure fuzzy string scoring — no Unity / no I/O dependencies.
    /// Linked into both the Package (used by PathResolver) and the CLI (used by CodeAnalysisCore).
    /// </summary>
    public static class FuzzyMatch
    {
        /// <summary>
        /// Similarity score 0..100. Higher = closer match.
        /// Layered: exact → prefix → substring → token overlap → acronym → edit distance.
        /// `needleLower` MUST be lowercased by caller.
        /// </summary>
        public static int Score(string candidate, string needleLower)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(needleLower)) return 0;
            string c = candidate.ToLowerInvariant();

            if (c == needleLower) return 100;
            if (c.StartsWith(needleLower, StringComparison.Ordinal)) return 85;
            if (c.Contains(needleLower)) return 70;
            // Reverse-contains: needle contains candidate. Require candidate to be ≥5 chars
            // AND ≥50% of needle length — prevents tiny tokens (PNG, UI, ID) matching everything.
            if (c.Length >= 5 && c.Length * 2 >= needleLower.Length && needleLower.Contains(c)) return 55;

            int score = 0;

            var cTokens = SplitTokens(c);
            var nTokens = SplitTokens(needleLower);
            int hits = 0;
            foreach (var nt in nTokens)
            {
                if (nt.Length < 2) continue;
                bool matched = false;
                foreach (var ct in cTokens)
                {
                    if (ct == nt) { hits++; matched = true; break; }
                    // Substring overlap: shorter token must be ≥4 chars AND ≥60% of longer
                    // token's length — stops "PNG"/"UI"/"ID" matching unrelated longer names.
                    int min = Math.Min(ct.Length, nt.Length);
                    int max = Math.Max(ct.Length, nt.Length);
                    if (min >= 4 && min * 5 >= max * 3 && (ct.Contains(nt) || nt.Contains(ct))) { hits++; matched = true; break; }
                }
                if (matched) continue;
                // Per-token typo tolerance — Levenshtein dist ≤1 (or ≤2 for ≥6-char tokens).
                if (nt.Length >= 4)
                {
                    foreach (var ct in cTokens)
                    {
                        if (ct.Length < 4 || Math.Abs(ct.Length - nt.Length) > 2) continue;
                        int d = LevenshteinDistance(ct, nt);
                        int allowed = nt.Length >= 6 ? 2 : 1;
                        if (d <= allowed) { hits++; break; }
                    }
                }
            }
            if (hits > 0 && nTokens.Count > 0)
                score = Math.Max(score, 30 + (40 * hits / nTokens.Count));

            // Acronym: needle = first letter of each candidate token (e.g. "pcd" → "PlayerControllerData").
            if (needleLower.Length >= 2 && cTokens.Count >= needleLower.Length)
            {
                bool acronym = true;
                for (int i = 0; i < needleLower.Length && i < cTokens.Count; i++)
                    if (cTokens[i].Length == 0 || cTokens[i][0] != needleLower[i]) { acronym = false; break; }
                if (acronym) score = Math.Max(score, 50);
            }

            // Edit distance on raw strings.
            if (Math.Abs(c.Length - needleLower.Length) <= 3 && c.Length <= 32 && needleLower.Length <= 32)
            {
                int dist = LevenshteinDistance(c, needleLower);
                int maxLen = Math.Max(c.Length, needleLower.Length);
                if (dist <= 2) score = Math.Max(score, 60 - (dist * 10));
                else if (dist * 100 / maxLen <= 30) score = Math.Max(score, 35);
            }

            // Compound match: strip separators from BOTH sides and re-test substring + edit
            // distance. Catches "Word Wrapping" vs "wordwrappng" where token-level fails
            // because the needle is one concatenated word.
            string cConcat = string.Concat(cTokens);
            string nConcat = string.Concat(nTokens);
            if (cConcat.Length >= 4 && nConcat.Length >= 4 && cConcat != c)
            {
                if (cConcat == nConcat) score = Math.Max(score, 90);
                else if (cConcat.Contains(nConcat) || nConcat.Contains(cConcat)) score = Math.Max(score, 65);
                else if (Math.Abs(cConcat.Length - nConcat.Length) <= 3 && cConcat.Length <= 40 && nConcat.Length <= 40)
                {
                    int d = LevenshteinDistance(cConcat, nConcat);
                    if (d <= 2) score = Math.Max(score, 55 - (d * 10));
                    else if (d * 100 / Math.Max(cConcat.Length, nConcat.Length) <= 25) score = Math.Max(score, 35);
                }
            }

            return score;
        }

        public static List<string> SplitTokens(string s)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(s)) return result;
            var current = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                bool sep = ch == ' ' || ch == '/' || ch == '_' || ch == '-' || ch == '.' || ch == '\\';
                bool boundary = i > 0 && char.IsUpper(ch) && !char.IsUpper(s[i - 1]);
                if (sep || boundary)
                {
                    if (current.Length > 0) { result.Add(current.ToString().ToLowerInvariant()); current.Clear(); }
                    if (sep) continue;
                }
                current.Append(ch);
            }
            if (current.Length > 0) result.Add(current.ToString().ToLowerInvariant());
            return result;
        }

        public static int LevenshteinDistance(string a, string b)
        {
            int[] prev = new int[b.Length + 1];
            int[] curr = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                (prev, curr) = (curr, prev);
            }
            return prev[b.Length];
        }
    }
}
