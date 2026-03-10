using System;
using System.Collections.Generic;

namespace clibridge4unity
{
    /// <summary>
    /// Flexible token-based argument parser for bridge commands.
    /// Case-insensitive, order-independent, singular/plural tolerant.
    /// Unrecognized tokens are collected as warnings (not errors).
    /// </summary>
    public class CommandArgs
    {
        public HashSet<string> Flags { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Options { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> Positional { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();

        public bool Has(string flag) => Flags.Contains(flag);
        public string Get(string key, string def = null) => Options.TryGetValue(key, out var v) ? v : def;

        public int GetInt(string key, int def = 0) =>
            Options.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : def;

        public long GetLong(string key, long def = 0) =>
            Options.TryGetValue(key, out var v) && long.TryParse(v, out var l) ? l : def;

        /// <summary>
        /// Returns warning line for unrecognized tokens, or empty string.
        /// Prepend to response so caller sees what was ignored.
        /// </summary>
        public string WarningPrefix()
        {
            if (Warnings.Count == 0) return "";
            return $"[ignored: {string.Join(", ", Warnings)}]\n";
        }

        /// <summary>
        /// True if no tokens were parsed at all.
        /// </summary>
        public bool IsEmpty => Flags.Count == 0 && Options.Count == 0 && Positional.Count == 0;

        /// <summary>
        /// Parse command data into flags, key:value options, and positional args.
        /// Case-insensitive. "error"/"errors" both match "errors". Any order.
        /// Unknown tokens go to Warnings (not errors).
        /// </summary>
        /// <param name="data">Raw command data string</param>
        /// <param name="knownFlags">Accepted flag names (e.g. "errors", "verbose", "all")</param>
        /// <param name="knownOptions">Accepted option keys (e.g. "last", "since")</param>
        public static CommandArgs Parse(string data, string[] knownFlags = null, string[] knownOptions = null)
        {
            var args = new CommandArgs();
            if (string.IsNullOrWhiteSpace(data)) return args;

            var flags = knownFlags ?? Array.Empty<string>();
            var options = knownOptions ?? Array.Empty<string>();

            foreach (var raw in data.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = raw.Trim();
                if (string.IsNullOrEmpty(token)) continue;

                // Check key:value or key=value option first
                int sepIdx = token.IndexOf(':');
                if (sepIdx <= 0) sepIdx = token.IndexOf('=');
                if (sepIdx > 0 && sepIdx < token.Length - 1)
                {
                    string key = token.Substring(0, sepIdx);
                    string value = token.Substring(sepIdx + 1);
                    string matchedOption = MatchKnown(key, options);
                    if (matchedOption != null)
                    {
                        args.Options[matchedOption] = value;
                        continue;
                    }
                }

                // Check flag
                string matchedFlag = MatchKnown(token, flags);
                if (matchedFlag != null)
                {
                    args.Flags.Add(matchedFlag);
                    continue;
                }

                // No schema = positional; has schema = warning
                if (flags.Length > 0 || options.Length > 0)
                    args.Warnings.Add(token);
                else
                    args.Positional.Add(token);
            }

            return args;
        }

        /// <summary>
        /// Parse as positional args only (no flag/option matching).
        /// All tokens become positional args.
        /// </summary>
        public static CommandArgs ParsePositional(string data)
        {
            return Parse(data);
        }

        /// <summary>
        /// Match a token against known names, case-insensitive, with singular/plural tolerance.
        /// Returns the canonical known name or null.
        /// </summary>
        private static string MatchKnown(string token, string[] known)
        {
            string lower = token.ToLowerInvariant();
            foreach (var name in known)
            {
                string nameLower = name.ToLowerInvariant();
                if (nameLower == lower) return name;

                // Singular/plural: "error" matches "errors", "errors" matches "error"
                if (lower.EndsWith("s") && nameLower == lower.Substring(0, lower.Length - 1)) return name;
                if (!lower.EndsWith("s") && nameLower == lower + "s") return name;
            }
            return null;
        }
    }
}
