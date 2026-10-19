using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace clibridge4unity;

/// <summary>Where Unity editors are installed and where the editor writes Editor.log, per OS.</summary>
static class UnityInstall
{
    /// <summary>The installed editor's `Editor` folder (holds `Data/` and the executable) for <paramref name="version"/>, or null.</summary>
    public static string FindEditorDir(string version)
    {
        string baseVersion = version.Split('f', 'a', 'b', 'p')[0];
        foreach (var root in InstallRoots())
        {
            if (!Directory.Exists(root)) continue;
            string exact = Path.Combine(root, version, "Editor");
            if (Directory.Exists(Path.Combine(exact, "Data"))) return exact;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (!Path.GetFileName(dir).StartsWith(baseVersion)) continue;
                string editorDir = Path.Combine(dir, "Editor");
                if (Directory.Exists(Path.Combine(editorDir, "Data"))) return editorDir;
            }
        }
        return null;
    }

    public static string Executable(string editorDir)
        => Path.Combine(editorDir, OperatingSystem.IsWindows() ? "Unity.exe" : "Unity");

    /// <summary>Hub's default install folder, its "Installs location" preference when set, and the
    /// folders running editors were launched from (installs the Hub never recorded).</summary>
    public static IEnumerable<string> InstallRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\Program Files\Unity\Hub\Editor";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor");
        }
        else
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Unity", "Hub", "Editor");
        }

        string secondary = null;
        try
        {
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub", "secondaryInstallPath.json");
            if (File.Exists(file)) secondary = File.ReadAllText(file).Trim().Trim('"').Replace(@"\\", @"\");
        }
        catch { }
        if (!string.IsNullOrEmpty(secondary)) yield return secondary;

        foreach (var root in RunningEditorRoots()) yield return root;
    }

    // <root>/<version>/Editor/Unity[.exe] -> <root>
    static List<string> RunningEditorRoots()
    {
        var roots = new List<string>();
        foreach (var proc in Process.GetProcessesByName("Unity"))
        {
            try
            {
                string versionDir = Path.GetDirectoryName(Path.GetDirectoryName(proc.MainModule?.FileName));
                string root = Path.GetDirectoryName(versionDir);
                if (root != null && !roots.Contains(root)) roots.Add(root);
            }
            catch { }
        }
        return roots;
    }

    /// <summary>Folder holding Editor.log / Editor-prev.log when the editor wasn't given -logFile.</summary>
    public static string EditorLogDir => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity", "Editor")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "unity3d");
}
