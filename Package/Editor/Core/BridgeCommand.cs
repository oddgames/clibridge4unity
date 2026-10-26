using System;

namespace clibridge4unity
{
    /// <summary>
    /// Marks a method as a bridge command accessible via clibridge4unity.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class BridgeCommandAttribute : Attribute
    {
        /// <summary>
        /// The command name (e.g., "PING", "STATUS"). Case-insensitive.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Short description shown in help.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Usage example (e.g., "ANALYZE ClassName.MethodName").
        /// </summary>
        public string Usage { get; set; }

        /// <summary>
        /// Whether this command requires the Unity main thread.
        /// </summary>
        public bool RequiresMainThread { get; set; }

        /// <summary>
        /// Whether this command streams output directly to the pipe.
        /// </summary>
        public bool Streaming { get; set; }

        /// <summary>
        /// Category for grouping in help (e.g., "Core", "Scene", "Assets").
        /// </summary>
        public string Category { get; set; } = "General";

        /// <summary>
        /// Timeout in seconds for operations that may cause Unity to reload assemblies.
        /// If > 0, the CLI will wait up to this many seconds for Unity to reconnect after executing the command.
        /// Use for commands like COMPILE and REFRESH that trigger assembly reloads.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 0;

        /// <summary>
        /// Seconds to wait for a result before answering "still running" instead of continuing to
        /// block. 0 (default) keeps the classic behaviour: wait for the result or the timeout.
        /// </summary>
        /// <remarks>
        /// For an operation with no upper bound — <c>ExecuteMenuItem</c>, a full
        /// <c>AssetDatabase.Refresh</c>, <c>ForceReserializeAssets</c> over a whole project — waiting
        /// for completion means the caller gets nothing at all until it finishes or the deadline
        /// expires, and a deadline expiry reads as failure even though the work succeeded. Worse, a
        /// menu item that opens a modal never returns and the caller waits out the full timeout for
        /// an error.
        ///
        /// With this set, the command is dispatched to the main thread as usual, but once the grace
        /// period elapses the caller is answered immediately with an accepted/still-running envelope
        /// naming the command and how to follow it. The work is NOT cancelled — it keeps running and
        /// its effects still land; only the caller stops waiting.
        /// </remarks>
        public int DetachAfterSeconds { get; set; } = 0;

        /// <summary>
        /// Opts the command into a caller-supplied <c>--timeout &lt;seconds&gt;</c> flag. 0 (default) = no flag.
        /// </summary>
        /// <remarks>
        /// When &gt; 0 and the command data carries <c>--timeout N</c> (or <c>--timeout=N</c>), the server's
        /// deadline — and the <c>__timeout</c> hint that sizes the CLI's read window — becomes
        /// <c>N + TimeoutFlagGraceSeconds</c> instead of <see cref="TimeoutSeconds"/>. The command parses the
        /// same flag itself with <see cref="TimeoutFlag"/> and applies N to its own work; the grace is the
        /// headroom that lets its reply (including a "did not finish within N s" error) get back before
        /// anything upstream gives up. Without it the two deadlines are equal and the caller's read window
        /// closes at the instant the answer is being written.
        /// </remarks>
        public int TimeoutFlagGraceSeconds { get; set; } = 0;

        /// <summary>
        /// Related commands suggested to the caller on successful responses.
        /// Appended as "Related: CMD1, CMD2, ..." so the AI is reminded of adjacent tools.
        /// </summary>
        public string[] RelatedCommands { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Alternate names that resolve to this command (case-insensitive).
        /// HELP lists them inline; lookups via any alias return the same CommandInfo.
        /// </summary>
        public string[] Aliases { get; set; } = Array.Empty<string>();

        public BridgeCommandAttribute(string name, string description)
        {
            Name = name?.ToUpperInvariant() ?? throw new ArgumentNullException(nameof(name));
            Description = description ?? throw new ArgumentNullException(nameof(description));
        }
    }

    /// <summary>
    /// The <c>--timeout &lt;seconds&gt;</c> / <c>--timeout=&lt;seconds&gt;</c> flag, parsed one way for the server
    /// (which sizes its deadline from it) and the command (which applies it). The CLI mirrors this pattern.
    /// Only a standalone token matches, so <c>--timeout</c> inside a longer word is left alone.
    /// </summary>
    public static class TimeoutFlag
    {
        /// <summary>Upper bound — a day. Keeps seconds * 1000 well inside an int.</summary>
        public const int MaxSeconds = 86400;

        static readonly System.Text.RegularExpressions.Regex Valued = new System.Text.RegularExpressions.Regex(
            @"(?<=^|\s)--timeout(?:=|\s+)(\d+)(?=\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        static readonly System.Text.RegularExpressions.Regex Bare = new System.Text.RegularExpressions.Regex(
            @"(?<=^|\s)--timeout(?==|\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>The requested seconds, if the data carries a valid flag. Leaves the data untouched.</summary>
        public static bool TryParse(string data, out int seconds)
        {
            seconds = 0;
            if (string.IsNullOrEmpty(data) || data.IndexOf("--timeout", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            var matches = Valued.Matches(data);
            if (matches.Count == 0) return false;
            return int.TryParse(matches[matches.Count - 1].Groups[1].Value, out seconds)
                && seconds >= 1 && seconds <= MaxSeconds;
        }

        /// <summary>
        /// Strips every <c>--timeout</c> occurrence from <paramref name="data"/>. <paramref name="seconds"/> is the
        /// last valid value, or <paramref name="defaultSeconds"/> when there is none; <paramref name="error"/> is
        /// set when a flag is present but malformed or out of range.
        /// </summary>
        public static string Extract(string data, int defaultSeconds, out int seconds, out bool specified, out string error)
        {
            seconds = defaultSeconds;
            specified = false;
            error = null;
            if (string.IsNullOrEmpty(data) || data.IndexOf("--timeout", StringComparison.OrdinalIgnoreCase) < 0)
                return data;

            var matches = Valued.Matches(data);
            if (matches.Count > 0)
            {
                string raw = matches[matches.Count - 1].Groups[1].Value;
                if (int.TryParse(raw, out int parsed) && parsed >= 1 && parsed <= MaxSeconds)
                {
                    seconds = parsed;
                    specified = true;
                }
                else
                {
                    error = $"--timeout must be a whole number of seconds from 1 to {MaxSeconds} (got '{raw}').";
                }
                data = Valued.Replace(data, "");
            }
            if (Bare.IsMatch(data))
            {
                error ??= "--timeout needs a value in seconds, e.g. --timeout 120 or --timeout=120.";
                data = Bare.Replace(data, "");
            }
            return data.Trim();
        }
    }
}
