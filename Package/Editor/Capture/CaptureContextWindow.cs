using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace clibridge4unity
{
    /// <summary>
    /// Assembles a paste-ready prompt out of a screen region plus whatever scene context you tick.
    ///
    /// The picker lives in the Editor rather than in the CLI because Unity already HAS the best
    /// hierarchy tree there is, and because the thing being described (a GameObject, its serialized
    /// fields, its broken reference) only exists inside the Editor. The CLI owns the half it is
    /// better at — a desktop-wide drag-select overlay, which an EditorWindow cannot draw.
    ///
    /// Nothing here runs unless the window is open: no [InitializeOnLoad], no update hook at rest,
    /// no cached scans. The EditorApplication.update subscription exists ONLY while a capture
    /// child-process is in flight, and unsubscribes the moment it exits — polling a Process here is
    /// not main-thread marshaling (which must always go through SynchronizationContext), it is a
    /// main-thread timer for a main-thread-owned child, and it is what keeps the Editor from
    /// freezing for however long the user takes to drag a rectangle.
    /// </summary>
    public class CaptureContextWindow : EditorWindow
    {
        const string CliName = "clibridge4unity";
        const string PackageName = "au.com.oddgames.clibridge4unity";

        enum Detail { Brief, Fields, FieldsAndRefs }

        // ─── UI state ─────────────────────────────────────────────────

        [SerializeField] string _ask = "";
        [SerializeField] string _shotPath;
        [SerializeField] string _videoPath;
        [SerializeField] string _sheetPath;
        [SerializeField] string _transcriptPath;
        [SerializeField] bool _transcribe = true;
        [SerializeField] Detail _detail = Detail.Fields;
        [SerializeField] bool _includeChildren;
        [SerializeField] bool _incConsole = true;
        [SerializeField] bool _incScene = true;
        [SerializeField] bool _incVersions;

        readonly HashSet<string> _ticked = new HashSet<string>();
        readonly HashSet<string> _expanded = new HashSet<string>();

        Vector2 _scrollTree, _scrollRoot;
        Texture2D _thumb;
        Process _capture;
        string _capturePath;
        string _status;

        // Ctrl+Shift+K, deliberately not a Print Screen chord: a RegisterHotKey claim wins
        // system-wide, so a menu item sharing that chord would silently never fire whenever the
        // capture daemon happened to be running.
        [MenuItem("Tools/CLI Bridge for Unity/Capture Context %#k")]
        public static void Open()
        {
            var w = GetWindow<CaptureContextWindow>("Capture Context");
            w.minSize = new Vector2(360, 420);
            w.SeedFromSelection();
            w.Show();
        }

        /// <summary>
        /// Pick up a capture taken with the global hotkey while this window was in the background.
        /// Focus-driven rather than polled — the daemon writes a latest.txt pointer precisely so
        /// this can be a single file read on an event that already happens, not an update hook.
        /// </summary>
        void OnFocus()
        {
            try
            {
                string pointer = Path.Combine(Path.GetTempPath(), "clibridge4unity", "captures", "latest.txt");
                if (!File.Exists(pointer)) return;
                string latest = File.ReadAllText(pointer).Trim();
                if (string.IsNullOrEmpty(latest) || !File.Exists(latest)) return;
                if (latest == _shotPath || latest == _videoPath) return;

                Attach(latest);
                _status = "Picked up a capture taken outside this window.";
                Repaint();
            }
            catch { /* the pointer is a convenience; never let it break the window */ }
        }

        /// <summary>
        /// Refresh the Record/Stop button while a detached recording runs. Throttled to 1 Hz and
        /// repainting only on an actual state change — Update fires ~10x/sec, and IsRecording does
        /// file I/O plus a process lookup, which is not something to do 10 times a second.
        /// </summary>
        void Update()
        {
            if (EditorApplication.timeSinceStartup - _lastRecCheck < 1.0) return;
            _lastRecCheck = EditorApplication.timeSinceStartup;

            bool rec = IsRecording();
            if (rec == _recWasOn) return;
            _recWasOn = rec;
            if (!rec) OnFocus();   // a recording just finished — pick up its artefacts
            Repaint();
        }

        double _lastRecCheck;
        bool _recWasOn;

        void OnDisable()
        {
            // Never leave the update hook armed — a stray subscription is exactly the kind of
            // per-frame cost the Editor Performance Mandate exists to prevent.
            EditorApplication.update -= PollCapture;
            if (_thumb != null) { DestroyImmediate(_thumb); _thumb = null; }
        }

        // ─── Layout ───────────────────────────────────────────────────

        void OnGUI()
        {
            _scrollRoot = EditorGUILayout.BeginScrollView(_scrollRoot);

            EditorGUILayout.LabelField("What do you want to ask?", EditorStyles.boldLabel);
            _ask = EditorGUILayout.TextArea(_ask, GUILayout.MinHeight(48));

            DrawScreenshotSection();
            DrawSceneSection();
            DrawExtrasSection();

            EditorGUILayout.EndScrollView();

            DrawFooter();
        }

        void DrawScreenshotSection()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Capture", EditorStyles.boldLabel);

            bool recording = IsRecording();

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_capture != null || recording))
                {
                    if (GUILayout.Button(_capture != null ? "Drag a region…" : "Screenshot"))
                        BeginCapture();
                }

                if (recording)
                {
                    var prev = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.90f, 0.30f, 0.24f);
                    if (GUILayout.Button("Stop recording")) RunCli("RECORD --stop", detached: false);
                    GUI.backgroundColor = prev;
                }
                else
                {
                    using (new EditorGUI.DisabledScope(_capture != null))
                    {
                        if (GUILayout.Button("Record"))
                            RunCli(_transcribe ? "RECORD --transcribe" : "RECORD", detached: true);
                    }
                }

                using (new EditorGUI.DisabledScope(_shotPath == null && _videoPath == null))
                {
                    if (GUILayout.Button("Clear", GUILayout.Width(56))) ClearAttachment();
                }
            }

            _transcribe = EditorGUILayout.ToggleLeft(
                new GUIContent("Transcribe narration (whisper)",
                    "Speak while recording and the words land in the prompt as text. "
                    + "First use downloads a ~148 MB model."),
                _transcribe);

            if (recording)
            {
                EditorGUILayout.HelpBox("Recording. Narrate what is wrong — that audio becomes the "
                    + "transcript section of the prompt.", MessageType.Info);
                return;
            }

            if (_thumb != null)
            {
                float w = Mathf.Min(_thumb.width, EditorGUIUtility.currentViewWidth - 30f);
                float h = w * _thumb.height / Mathf.Max(1, _thumb.width);
                var r = GUILayoutUtility.GetRect(w, Mathf.Min(h, 220f), GUILayout.ExpandWidth(false));
                GUI.DrawTexture(r, _thumb, ScaleMode.ScaleToFit);
            }

            if (_videoPath != null)
            {
                EditorGUILayout.LabelField($"{Path.GetFileName(_videoPath)}", EditorStyles.miniLabel);
                // Say plainly what will and will not reach the assistant — the video itself won't.
                EditorGUILayout.LabelField(
                    $"frames: {(_sheetPath != null ? "yes" : "none")}    "
                    + $"transcript: {(_transcriptPath != null ? "yes" : "none")}",
                    EditorStyles.miniLabel);
                if (_sheetPath == null && _transcriptPath == null)
                    EditorGUILayout.HelpBox("Only the .mp4 exists. An assistant cannot watch video — "
                        + "without frames or a transcript this adds nothing to the prompt.", MessageType.Warning);
            }
            else if (_shotPath != null)
            {
                EditorGUILayout.LabelField($"{Path.GetFileName(_shotPath)}", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.HelpBox("Nothing attached. Screenshot grabs a region; Record captures "
                    + "a region with audio and reduces it to frames + a transcript.", MessageType.None);
            }
        }

        void ClearAttachment()
        {
            _shotPath = _videoPath = _sheetPath = _transcriptPath = null;
            if (_thumb != null) { DestroyImmediate(_thumb); _thumb = null; }
        }

        /// <summary>
        /// Recording runs in a detached child process, so the panel's view of it comes from the
        /// recorder's pid file rather than a handle it owns — that way a recording started from the
        /// tray or another terminal shows up here too.
        /// </summary>
        static bool IsRecording()
        {
            try
            {
                string pidFile = Path.Combine(Path.GetTempPath(), "clibridge4unity", "captures", "recorder.pid");
                if (!File.Exists(pidFile)) return false;
                if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid)) return false;
                var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch { return false; }
        }

        /// <summary>Attach a PNG or an MP4, picking up the recorder's sibling artefacts for video.</summary>
        void Attach(string path)
        {
            ClearAttachment();
            if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                _videoPath = path;
                string baseName = path.Substring(0, path.Length - 4);
                string sheet = baseName + ".frames.png";
                string txt = baseName + ".transcript.txt";
                if (File.Exists(sheet)) { _sheetPath = sheet; LoadThumb(sheet); }
                if (File.Exists(txt)) _transcriptPath = txt;
            }
            else
            {
                _shotPath = path;
                LoadThumb(path);
            }
        }

        void RunCli(string args, bool detached)
        {
            string exe = ResolveCli();
            if (exe == null) { _status = "clibridge4unity not found on PATH — run the Setup Wizard first."; return; }
            try
            {
                var p = Process.Start(new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (!detached) p.WaitForExit(10000);
                _status = detached ? "Recording started — narrate the problem, then Stop." : "Stopping…";
            }
            catch (Exception ex) { _status = $"Could not run '{args}': {ex.Message}"; }
            Repaint();
        }

        void DrawSceneSection()
        {
            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Include from the scene", EditorStyles.boldLabel);
                if (GUILayout.Button("Use selection", EditorStyles.miniButton, GUILayout.Width(90)))
                    SeedFromSelection();
                if (GUILayout.Button("None", EditorStyles.miniButton, GUILayout.Width(50)))
                    _ticked.Clear();
            }

            _scrollTree = EditorGUILayout.BeginScrollView(_scrollTree, GUILayout.MinHeight(120), GUILayout.MaxHeight(260));
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                EditorGUILayout.LabelField("No scene loaded.", EditorStyles.miniLabel);
            }
            else
            {
                // GetRootGameObjects returns inactive roots too, which is the point — the object
                // you want to ask about is very often the one that is disabled.
                foreach (var root in scene.GetRootGameObjects())
                    DrawNode(root.transform, 0);
            }
            EditorGUILayout.EndScrollView();

            _detail = (Detail)EditorGUILayout.EnumPopup("Detail", _detail);
            _includeChildren = EditorGUILayout.Toggle("Include children", _includeChildren);
        }

        void DrawNode(Transform t, int indent)
        {
            string path = PathOf(t);
            bool hasKids = t.childCount > 0;
            bool open = _expanded.Contains(path);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(indent * 12f);

                if (hasKids)
                {
                    bool now = EditorGUILayout.Foldout(open, GUIContent.none, true, EditorStyles.foldout);
                    if (now != open)
                    {
                        if (now) _expanded.Add(path); else _expanded.Remove(path);
                    }
                    open = now;
                }
                else
                {
                    GUILayout.Space(14f);
                }

                bool was = _ticked.Contains(path);
                bool tick = GUILayout.Toggle(was, GUIContent.none, GUILayout.Width(16));
                if (tick != was)
                {
                    if (tick) _ticked.Add(path); else _ticked.Remove(path);
                }

                var style = t.gameObject.activeInHierarchy ? EditorStyles.label : EditorStyles.miniLabel;
                GUILayout.Label(new GUIContent(t.name, t.gameObject.activeInHierarchy ? "" : "inactive"), style);
                GUILayout.FlexibleSpace();
            }

            if (hasKids && open)
                for (int i = 0; i < t.childCount; i++)
                    DrawNode(t.GetChild(i), indent + 1);
        }

        void DrawExtrasSection()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Also include", EditorStyles.boldLabel);
            _incConsole = EditorGUILayout.Toggle("Console errors", _incConsole);
            _incScene = EditorGUILayout.Toggle("Scene + play mode", _incScene);
            _incVersions = EditorGUILayout.Toggle("Unity / project version", _incVersions);
        }

        void DrawFooter()
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Copy Prompt", GUILayout.Height(26)))
                {
                    string prompt = BuildPrompt();
                    EditorGUIUtility.systemCopyBuffer = prompt;
                    _status = $"Copied {prompt.Length:N0} chars — paste into your assistant.";
                }
                GUILayout.Label($"{_ticked.Count} object(s)", EditorStyles.miniLabel, GUILayout.Width(80));
            }
            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.LabelField(_status, EditorStyles.miniLabel);
        }

        // ─── Selection seeding ────────────────────────────────────────

        void SeedFromSelection()
        {
            _ticked.Clear();
            foreach (var go in Selection.gameObjects)
            {
                if (go == null || !go.scene.IsValid()) continue;
                string path = PathOf(go.transform);
                _ticked.Add(path);
                // Open the tree down to it, otherwise a deep tick is invisible.
                for (var p = go.transform.parent; p != null; p = p.parent)
                    _expanded.Add(PathOf(p));
            }
            _status = _ticked.Count > 0 ? $"Seeded {_ticked.Count} object(s) from selection." : "Nothing selected in the Hierarchy.";
            Repaint();
        }

        static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent)
                sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        // ─── Region capture (shells out to the CLI) ───────────────────

        void BeginCapture()
        {
            string exe = ResolveCli();
            if (exe == null)
            {
                _status = "clibridge4unity not found on PATH — run the Setup Wizard first.";
                return;
            }

            _capturePath = Path.Combine(Path.GetTempPath(), "clibridge4unity", "captures",
                $"panel_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_capturePath));
                _capture = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"CAPTURE --out \"{_capturePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                _status = "Drag a rectangle. Esc or right-click cancels.";
                EditorApplication.update += PollCapture;
            }
            catch (Exception ex)
            {
                _capture = null;
                _status = $"Could not start capture: {ex.Message}";
            }
            Repaint();
        }

        void PollCapture()
        {
            if (_capture == null) { EditorApplication.update -= PollCapture; return; }
            if (!_capture.HasExited) return;

            EditorApplication.update -= PollCapture;
            int code = _capture.ExitCode;
            _capture.Dispose();
            _capture = null;

            if (code == 0 && File.Exists(_capturePath))
            {
                Attach(_capturePath);
                _status = "Region attached.";
            }
            else
            {
                _status = code == 1 ? "Capture cancelled." : $"Capture failed (exit {code}).";
            }
            Repaint();
        }

        void LoadThumb(string path)
        {
            if (_thumb != null) DestroyImmediate(_thumb);
            _thumb = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try { _thumb.LoadImage(File.ReadAllBytes(path)); }
            catch (Exception ex)
            {
                DestroyImmediate(_thumb); _thumb = null;
                Debug.LogWarning($"[Bridge] Could not load capture preview: {ex.Message}");
            }
        }

        static string ResolveCli()
        {
            string exe = Application.platform == RuntimePlatform.WindowsEditor ? CliName + ".exe" : CliName;

            var candidates = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clibridge4unity", exe),
                Path.GetFullPath($"Packages/{PackageName}/Tools/win-x64/{exe}"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            // Fall back to PATH resolution by the process launcher itself.
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try { if (File.Exists(Path.Combine(dir.Trim(), exe))) return Path.Combine(dir.Trim(), exe); }
                catch { }
            }
            return null;
        }

        // ─── Prompt assembly ──────────────────────────────────────────

        string BuildPrompt()
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(_ask))
                sb.AppendLine(_ask.Trim()).AppendLine();

            if (_shotPath != null)
            {
                sb.AppendLine("## Screenshot").AppendLine();
                sb.AppendLine($"`{_shotPath}`").AppendLine();
            }

            if (_videoPath != null)
            {
                sb.AppendLine("## Screen recording").AppendLine();
                // The mp4 is listed for the human. The frames are what the assistant can read, so
                // they are labelled as the thing to open rather than buried under the video path.
                if (_sheetPath != null)
                {
                    sb.AppendLine("Sampled frames, left-to-right then top-to-bottom (open this):");
                    sb.AppendLine($"`{_sheetPath}`").AppendLine();
                }
                sb.AppendLine($"Source video (not readable by an assistant): `{_videoPath}`").AppendLine();

                if (_transcriptPath != null)
                {
                    string text = null;
                    try { text = File.ReadAllText(_transcriptPath); } catch { }
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.AppendLine("### Narration transcript").AppendLine();
                        sb.AppendLine("```");
                        sb.AppendLine(text.TrimEnd());
                        sb.AppendLine("```").AppendLine();
                    }
                }
            }

            if (_incScene)
            {
                var scene = SceneManager.GetActiveScene();
                sb.AppendLine("## Editor state").AppendLine();
                sb.AppendLine($"- Scene: `{(string.IsNullOrEmpty(scene.path) ? scene.name : scene.path)}`");
                sb.AppendLine($"- Play mode: {(EditorApplication.isPlaying ? (EditorApplication.isPaused ? "playing (paused)" : "playing") : "stopped")}");
                if (_incVersions)
                {
                    sb.AppendLine($"- Unity: {Application.unityVersion}");
                    sb.AppendLine($"- Product: {Application.productName}");
                }
                sb.AppendLine();
            }
            else if (_incVersions)
            {
                sb.AppendLine("## Versions").AppendLine();
                sb.AppendLine($"- Unity: {Application.unityVersion}");
                sb.AppendLine($"- Product: {Application.productName}").AppendLine();
            }

            if (_ticked.Count > 0)
            {
                sb.AppendLine("## Selected objects").AppendLine();
                string flags = _detail == Detail.Brief ? " --brief"
                             : _detail == Detail.FieldsAndRefs ? " --refs" : "";
                if (_includeChildren) flags += " --children";

                foreach (var path in _ticked)
                {
                    sb.AppendLine($"### `{path}`").AppendLine();
                    string dump = RunCommand("INSPECTOR", path + flags);
                    sb.AppendLine("```");
                    sb.AppendLine(string.IsNullOrWhiteSpace(dump) ? "(INSPECTOR returned nothing — object may have been destroyed)" : dump.TrimEnd());
                    sb.AppendLine("```").AppendLine();
                }
            }

            if (_incConsole)
            {
                string logs = RunCommand("LOG", "errors");
                if (!string.IsNullOrWhiteSpace(logs))
                {
                    sb.AppendLine("## Console errors").AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(logs.TrimEnd());
                    sb.AppendLine("```").AppendLine();
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Invoke a registered [BridgeCommand] in-process. Going through CommandRegistry's
        /// MethodInfo rather than referencing the command assemblies keeps this window's asmdef
        /// down to Core alone — adding a reference to Commands.Component and Commands.Core here
        /// would drag those into every recompile of this file, for two calls.
        ///
        /// Direct invoke, not CommandRegistry.ExecuteCommand: we are already on the main thread,
        /// and INSPECTOR/LOG are both plain synchronous string methods.
        /// </summary>
        static string RunCommand(string name, string data)
        {
            try
            {
                CommandRegistry.Initialize();
                var info = CommandRegistry.GetCommand(name);
                if (info?.Method == null) return $"({name} is not registered — is the bridge package compiled?)";

                ParameterInfo[] ps = info.Method.GetParameters();
                object[] args = ps.Length == 0 ? Array.Empty<object>() : new object[] { data };
                object result = info.Method.Invoke(info.Instance, args);
                return result as string ?? $"({name} returned {result?.GetType().Name ?? "null"}, not text)";
            }
            catch (TargetInvocationException ex)
            {
                return $"({name} failed: {ex.InnerException?.Message ?? ex.Message})";
            }
            catch (Exception ex)
            {
                return $"({name} failed: {ex.Message})";
            }
        }
    }
}
