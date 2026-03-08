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

        public BridgeCommandAttribute(string name, string description)
        {
            Name = name?.ToUpperInvariant() ?? throw new ArgumentNullException(nameof(name));
            Description = description ?? throw new ArgumentNullException(nameof(description));
        }
    }
}
