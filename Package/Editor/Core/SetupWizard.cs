using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace clibridge4unity
{
    /// <summary>
    /// Setup wizard that checks if CLI is installed and guides user through installation.
    /// </summary>
    [InitializeOnLoad]
    public class SetupWizard : EditorWindow
    {
        private static readonly string CLI_NAME = "clibridge4unity";
        private static readonly string PREF_KEY = "CliBridge_SetupComplete";
        private static readonly string PREF_DISMISSED = "CliBridge_SetupDismissed";

        private static string installPath;
        private static bool cliInPath;
        private Vector2 scrollPosition;

        private static readonly string PREF_MISMATCH_DISMISSED = "CliBridge_MismatchDismissed";

        static SetupWizard()
        {
            EditorApplication.delayCall += CheckSetup;
        }

        static void CheckSetup()
        {
            // Don't show if already set up or dismissed
            if (EditorPrefs.GetBool(PREF_KEY, false) || EditorPrefs.GetBool(PREF_DISMISSED, false))
            {
                // Still check for version mismatch even if setup is complete
                CheckVersionMismatch();
                return;
            }

            // Check if CLI is in PATH
            if (IsCliInPath())
            {
                EditorPrefs.SetBool(PREF_KEY, true);
                CheckVersionMismatch();
                return;
            }

            // Show setup wizard
            EditorApplication.delayCall += () =>
            {
                GetWindow<SetupWizard>("CLI Bridge Setup", true);
            };
        }

        static void CheckVersionMismatch()
        {
            // Run on background thread to not block editor
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string cliVersion = GetCliVersionString();
                    if (string.IsNullOrEmpty(cliVersion)) return;

                    string packageVersion = GetPackageVersion();
                    if (string.IsNullOrEmpty(packageVersion)) return;

                    if (cliVersion != packageVersion)
                    {
                        // Check if user already dismissed this specific mismatch
                        string dismissedPair = SessionState.GetString(PREF_MISMATCH_DISMISSED, "");
                        string currentPair = $"{cliVersion}|{packageVersion}";
                        if (dismissedPair == currentPair) return;

                        // Show dialog on main thread
                        EditorApplication.delayCall += () =>
                        {
                            bool update = EditorUtility.DisplayDialog(
                                "CLI Bridge Version Mismatch",
                                $"The CLI tool on PATH is v{cliVersion} but the Unity package is v{packageVersion}.\n\n" +
                                "This can cause compatibility issues.\n\n" +
                                "Click 'Update CLI' to copy the matching version from the package.",
                                "Update CLI",
                                "Ignore");

                            if (update)
                            {
                                InstallCliSilent();
                            }
                            else
                            {
                                SessionState.SetString(PREF_MISMATCH_DISMISSED, currentPair);
                            }
                        };
                    }
                }
                catch { }
            });
        }

        /// <summary>
        /// Returns just the version number string (e.g. "1.0.8") from the CLI.
        /// </summary>
        static string GetCliVersionString()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = CLI_NAME,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(2000);
                    // Output is "clibridge4unity version X.Y.Z" — extract version
                    var parts = output.Trim().Split(' ');
                    return parts.Length >= 3 ? parts[2].Trim() : null;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// Reads the package version from package.json.
        /// </summary>
        static string GetPackageVersion()
        {
            try
            {
                string packagePath = Path.GetFullPath("Packages/au.com.oddgames.clibridge4unity/package.json");
                if (!File.Exists(packagePath)) return null;
                string json = File.ReadAllText(packagePath);
                // Simple parse: find "version": "X.Y.Z"
                var match = System.Text.RegularExpressions.Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Silently installs the CLI from the package (no wizard UI).
        /// </summary>
        static void InstallCliSilent()
        {
            try
            {
                string packagePath = Path.GetFullPath("Packages/au.com.oddgames.clibridge4unity");
                string destDir = GetDefaultInstallPath();

#if UNITY_EDITOR_WIN
                string sourceExe = Path.Combine(packagePath, "Tools", "win-x64", "clibridge4unity.exe");
                string destExe = Path.Combine(destDir, "clibridge4unity.exe");
#elif UNITY_EDITOR_OSX
                string sourceExe = Path.Combine(packagePath, "Tools", "osx-x64", "clibridge4unity");
                string destExe = Path.Combine(destDir, "clibridge4unity");
#else
                string sourceExe = Path.Combine(packagePath, "Tools", "linux-x64", "clibridge4unity");
                string destExe = Path.Combine(destDir, "clibridge4unity");
#endif

                if (!File.Exists(sourceExe))
                {
                    Debug.LogWarning($"[Bridge] CLI exe not found in package: {sourceExe}");
                    return;
                }

                Directory.CreateDirectory(destDir);

                // Rename-swap to handle locked exe
                string oldExe = destExe + ".old";
                if (File.Exists(destExe))
                {
                    if (File.Exists(oldExe)) File.Delete(oldExe);
                    File.Move(destExe, oldExe);
                }
                File.Copy(sourceExe, destExe, true);
                if (File.Exists(oldExe)) try { File.Delete(oldExe); } catch { }

                string newVersion = GetCliVersionString();
                Debug.Log($"[Bridge] CLI updated to v{newVersion}");
                EditorUtility.DisplayDialog("CLI Bridge Updated",
                    $"CLI tool updated to v{newVersion}.\n\nLocation: {destExe}",
                    "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Bridge] CLI update failed: {ex.Message}");
                EditorUtility.DisplayDialog("Update Failed",
                    $"Could not update CLI tool:\n{ex.Message}",
                    "OK");
            }
        }

        [MenuItem("Tools/CLI Bridge for Unity/Setup Wizard")]
        public static void ShowSetupWizard()
        {
            GetWindow<SetupWizard>("CLI Bridge Setup", true);
        }

        [MenuItem("Tools/CLI Bridge for Unity/Check Installation")]
        public static void CheckInstallation()
        {
            if (IsCliInPath())
            {
                EditorUtility.DisplayDialog("CLI Bridge",
                    "CLI tool is installed and available in PATH.\n\n" +
                    $"Version: {GetCliVersion()}",
                    "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("CLI Bridge",
                    "CLI tool is not found in PATH.\n\n" +
                    "Please run the Setup Wizard to install it.",
                    "OK");
            }
        }

        void OnEnable()
        {
            minSize = new Vector2(500, 400);
            maxSize = new Vector2(500, 400);
            cliInPath = IsCliInPath();
            installPath = GetDefaultInstallPath();
        }

        void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            GUILayout.Space(10);

            // Title
            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleCenter
            };
            EditorGUILayout.LabelField("CLI Bridge for Unity - Setup", titleStyle);

            GUILayout.Space(10);

            // Check status
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Installation Status", EditorStyles.boldLabel);

            if (cliInPath)
            {
                EditorGUILayout.LabelField("✓ CLI tool is installed and in PATH", new GUIStyle(EditorStyles.label) { normal = { textColor = Color.green } });
                EditorGUILayout.LabelField($"Version: {GetCliVersion()}");
            }
            else
            {
                EditorGUILayout.LabelField("✗ CLI tool not found in PATH", new GUIStyle(EditorStyles.label) { normal = { textColor = Color.yellow } });
            }
            EditorGUILayout.EndVertical();

            GUILayout.Space(10);

            if (!cliInPath)
            {
                // Installation instructions
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Installation", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("The CLI tool will be installed to:");
                EditorGUILayout.SelectableLabel(installPath, EditorStyles.textField, GUILayout.Height(18));

                GUILayout.Space(5);

                if (GUILayout.Button("Install CLI Tool", GUILayout.Height(30)))
                {
                    InstallCli();
                }

                GUILayout.Space(5);

                EditorGUILayout.HelpBox(
                    "After installation, you may need to:\n" +
                    GetPlatformInstructions(),
                    MessageType.Info);

                EditorGUILayout.EndVertical();

                GUILayout.Space(10);

                // Manual installation
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Manual Installation", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("You can also install manually:");

                if (GUILayout.Button("Open Package Tools Folder"))
                {
                    OpenToolsFolder();
                }

                EditorGUILayout.LabelField("Then copy the executable to a location in your PATH.");
                EditorGUILayout.EndVertical();
            }
            else
            {
                // Already installed - show usage
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Quick Start", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Run these commands from your project directory:");

                GUILayout.Space(5);
                EditorGUILayout.SelectableLabel("clibridge4unity PING", EditorStyles.textField, GUILayout.Height(18));
                EditorGUILayout.SelectableLabel("clibridge4unity STATUS", EditorStyles.textField, GUILayout.Height(18));
                EditorGUILayout.SelectableLabel("clibridge4unity -h", EditorStyles.textField, GUILayout.Height(18));

                EditorGUILayout.EndVertical();

                GUILayout.Space(10);

                if (GUILayout.Button("Update CLI Tool", GUILayout.Height(25)))
                {
                    InstallCli();
                }
            }

            GUILayout.FlexibleSpace();

            // Bottom buttons
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Documentation"))
            {
                Application.OpenURL("https://github.com/oddgames/clibridge4unity");
            }

            if (!cliInPath && GUILayout.Button("Remind Me Later"))
            {
                EditorPrefs.SetBool(PREF_DISMISSED, true);
                Close();
            }

            if (cliInPath && GUILayout.Button("Close"))
            {
                EditorPrefs.SetBool(PREF_KEY, true);
                Close();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();
        }

        static bool IsCliInPath()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = CLI_NAME,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    process.WaitForExit(2000);
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        static string GetCliVersion()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = CLI_NAME,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    return output.Trim();
                }
            }
            catch
            {
                return "Unknown";
            }
        }

        static string GetDefaultInstallPath()
        {
#if UNITY_EDITOR_WIN
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clibridge4unity");
#else
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
#endif
        }

        static string GetPlatformInstructions()
        {
#if UNITY_EDITOR_WIN
            return "Windows: Add the installation folder to your PATH environment variable, or restart your terminal.";
#elif UNITY_EDITOR_OSX
            return "macOS: Make sure ~/.local/bin is in your PATH. Add this to ~/.zshrc or ~/.bash_profile:\nexport PATH=\"$HOME/.local/bin:$PATH\"";
#else
            return "Linux: Make sure ~/.local/bin is in your PATH. Add this to ~/.bashrc:\nexport PATH=\"$HOME/.local/bin:$PATH\"";
#endif
        }

        static void AddToPath(string directory)
        {
#if UNITY_EDITOR_WIN
            // Add to User PATH on Windows
            try
            {
                string userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";

                // Check if already in PATH
                if (userPath.Split(';').Any(p => p.Trim().Equals(directory, StringComparison.OrdinalIgnoreCase)))
                {
                    Debug.Log($"[CLI Bridge] Directory already in PATH: {directory}");
                    return;
                }

                // Add to PATH
                string newPath = string.IsNullOrEmpty(userPath) ? directory : $"{userPath};{directory}";
                Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.User);

                Debug.Log($"[CLI Bridge] Added to PATH: {directory}");
                Debug.Log("[CLI Bridge] You may need to restart your terminal for PATH changes to take effect.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CLI Bridge] Could not add to PATH automatically: {ex.Message}");
            }
#else
            // On macOS/Linux, add to shell config file
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string[] shellConfigs = new[] {
                    Path.Combine(home, ".zshrc"),
                    Path.Combine(home, ".bashrc"),
                    Path.Combine(home, ".bash_profile")
                };

                string pathLine = $"\nexport PATH=\"$HOME/.local/bin:$PATH\"";
                bool added = false;

                foreach (var configFile in shellConfigs)
                {
                    if (File.Exists(configFile))
                    {
                        string content = File.ReadAllText(configFile);

                        // Check if already added
                        if (content.Contains("$HOME/.local/bin") || content.Contains("~/.local/bin"))
                        {
                            Debug.Log($"[CLI Bridge] PATH already configured in {Path.GetFileName(configFile)}");
                            return;
                        }

                        // Add to first config file found
                        if (!added)
                        {
                            File.AppendAllText(configFile, pathLine + "\n");
                            Debug.Log($"[CLI Bridge] Added PATH export to {Path.GetFileName(configFile)}");
                            Debug.Log("[CLI Bridge] Run 'source " + Path.GetFileName(configFile) + "' or restart your terminal.");
                            added = true;
                            break;
                        }
                    }
                }

                if (!added)
                {
                    // Create .bashrc if none exist
                    string bashrc = Path.Combine(home, ".bashrc");
                    File.WriteAllText(bashrc, pathLine + "\n");
                    Debug.Log($"[CLI Bridge] Created .bashrc with PATH export");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CLI Bridge] Could not modify shell config: {ex.Message}");
            }
#endif
        }

        void InstallCli()
        {
            try
            {
                // Find the CLI executable in the package
                string packagePath = Path.GetFullPath("Packages/au.com.oddgames.clibridge4unity");
                string toolsPath = Path.Combine(packagePath, "Tools");

#if UNITY_EDITOR_WIN
                string sourceExe = Path.Combine(toolsPath, "win-x64", "clibridge4unity.exe");
                string destExe = Path.Combine(installPath, "clibridge4unity.exe");
#elif UNITY_EDITOR_OSX
                string sourceExe = Path.Combine(toolsPath, "osx-x64", "clibridge4unity");
                string destExe = Path.Combine(installPath, "clibridge4unity");
#else
                string sourceExe = Path.Combine(toolsPath, "linux-x64", "clibridge4unity");
                string destExe = Path.Combine(installPath, "clibridge4unity");
#endif

                if (!File.Exists(sourceExe))
                {
                    EditorUtility.DisplayDialog("Error",
                        $"CLI executable not found in package:\n{sourceExe}\n\n" +
                        "Please reinstall the package or contact support.",
                        "OK");
                    return;
                }

                // Create install directory if it doesn't exist
                Directory.CreateDirectory(installPath);

                // Copy executable
                File.Copy(sourceExe, destExe, true);

#if !UNITY_EDITOR_WIN
                // Make executable on Unix
                var chmodInfo = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{destExe}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(chmodInfo)?.WaitForExit();
#endif

                // Add to PATH automatically
                AddToPath(installPath);

                // Refresh status
                cliInPath = IsCliInPath();

                if (cliInPath)
                {
                    EditorUtility.DisplayDialog("Success",
                        "CLI tool installed successfully!\n\n" +
                        $"Location: {destExe}\n\n" +
                        "PATH has been updated automatically.\n" +
                        "Restart your terminal and try: clibridge4unity --version",
                        "OK");
                    EditorPrefs.SetBool(PREF_KEY, true);
                }
                else
                {
                    EditorUtility.DisplayDialog("Installation Complete",
                        $"CLI tool copied to:\n{destExe}\n\n" +
                        "PATH has been updated. Please:\n" +
                        "1. Restart your terminal\n" +
#if UNITY_EDITOR_WIN
                        "2. Try: clibridge4unity --version\n\n" +
                        "If it still doesn't work, log out and log back in to refresh environment variables.",
#else
                        "2. Run: source ~/.bashrc (or ~/.zshrc)\n" +
                        "3. Try: clibridge4unity --version",
#endif
                        "OK");
                }

                Repaint();
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Installation Error",
                    $"Failed to install CLI tool:\n{ex.Message}",
                    "OK");
            }
        }

        void OpenToolsFolder()
        {
            string packagePath = Path.GetFullPath("Packages/au.com.oddgames.clibridge4unity");
            string toolsPath = Path.Combine(packagePath, "Tools");

            if (Directory.Exists(toolsPath))
            {
                EditorUtility.RevealInFinder(toolsPath);
            }
            else
            {
                EditorUtility.DisplayDialog("Error",
                    $"Tools folder not found:\n{toolsPath}",
                    "OK");
            }
        }
    }
}
