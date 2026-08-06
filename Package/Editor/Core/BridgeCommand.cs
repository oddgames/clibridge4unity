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
}
