using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace clibridge4unity;

/// <summary>
/// Region screen recording with audio, and — the part that actually matters — turning the result
/// into something an assistant can read.
///
/// An .mp4 in a prompt is inert: models read images and text, they do not watch video or hear
/// audio. So a finished recording is post-processed into two artefacts that DO carry into a
/// prompt — a contact sheet of sampled frames, and a transcript of the narration — with the video
/// kept alongside for the human. Skipping that step would produce a feature that looks like it
/// works and communicates nothing.
///
/// ffmpeg is required and deliberately not bundled: a full build is ~80 MB against a ~40 MB exe,
/// and anyone doing Unity work on Windows can `scoop install ffmpeg`. Absence is reported with the
/// install line, not a stack trace.
/// </summary>
static class ScreenRecorder
{
    // ─── Paths / process state ────────────────────────────────────────

    static string Dir => RegionCapture.CapturesDir;
    static string PidFile => Path.Combine(Dir, "recorder.pid");
    static string StopFile => Path.Combine(Dir, "recorder.stop");
    static string TargetFile => Path.Combine(Dir, "recorder.target");

    static string WhisperDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clibridge4unity", "whisper");

    // ggml-base.en: the smallest model that reliably transcribes a muttered bug report.
    // tiny.en is half the size and drops exactly the technical nouns this is for.
    const string WhisperModel = "ggml-base.en.bin";
    const string WhisperUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin";

    // ─── Entry ────────────────────────────────────────────────────────

    public static int Run(string args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Error: RECORD is Windows-only.");
            return 1;
        }

        string outPath = null, audio = "both";
        bool full = false, transcribe = false, noSheet = false;
        int seconds = 0, fps = 30;

        var parts = (args ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i].ToLowerInvariant();
            if ((p == "--out" || p == "-o") && i + 1 < parts.Length) outPath = parts[++i].Trim('"');
            else if (p == "--audio" && i + 1 < parts.Length) audio = parts[++i].ToLowerInvariant();
            else if (p == "--seconds" && i + 1 < parts.Length) int.TryParse(parts[++i], out seconds);
            else if (p == "--fps" && i + 1 < parts.Length) int.TryParse(parts[++i], out fps);
            else if (p == "--full") full = true;
            else if (p == "--transcribe") transcribe = true;
            else if (p == "--no-sheet") noSheet = true;
            else if (p == "--stop" || p == "stop") return Stop();
            else if (p == "--status" || p == "status") return Status();
            else if (p == "--devices" || p == "devices") return ListDevices();
        }

        return Record(outPath, full, audio, seconds, fps, transcribe, noSheet);
    }

    // ─── ffmpeg discovery ─────────────────────────────────────────────

    public static string FindFfmpeg() => FindOnPath("ffmpeg.exe");
    static string FindFfprobe() => FindOnPath("ffprobe.exe");

    static string FindOnPath(string exe)
    {
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                string c = Path.Combine(dir.Trim(), exe);
                if (File.Exists(c)) return c;
            }
            catch { }
        }
        return null;
    }

    static bool RequireFfmpeg(out string ffmpeg)
    {
        ffmpeg = FindFfmpeg();
        if (ffmpeg != null) return true;
        Console.Error.WriteLine("Error: ffmpeg not found on PATH — RECORD needs it for screen + audio capture.");
        Console.Error.WriteLine("Install one of:");
        Console.Error.WriteLine("  scoop install ffmpeg");
        Console.Error.WriteLine("  winget install Gyan.FFmpeg");
        return false;
    }

    // ─── Audio device discovery ───────────────────────────────────────

    /// <summary>
    /// DirectShow device names are machine-specific strings, so they are discovered rather than
    /// configured. System audio needs a loopback device that Windows does not ship: the usual
    /// providers are screen-capture-recorder's "virtual-audio-capturer" or a VB-Cable. Its absence
    /// is a normal outcome, not an error — we fall back to mic-only and say so.
    /// </summary>
    static (string mic, string system) FindAudioDevices()
    {
        string mic = null, sys = null;
        string ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return (null, null);

        try
        {
            var psi = new ProcessStartInfo(ffmpeg, "-hide_banner -f dshow -list_devices true -i dummy")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(15000);

            foreach (var raw in err.Split('\n'))
            {
                // Lines look like:  [in#0 @ ...] "Device Name" (audio)
                if (!raw.Contains("(audio)")) continue;
                int a = raw.IndexOf('"'); if (a < 0) continue;
                int b = raw.IndexOf('"', a + 1); if (b < 0) continue;
                string name = raw.Substring(a + 1, b - a - 1);

                bool loopback = name.IndexOf("virtual-audio", StringComparison.OrdinalIgnoreCase) >= 0
                             || name.IndexOf("stereo mix", StringComparison.OrdinalIgnoreCase) >= 0
                             || name.IndexOf("cable output", StringComparison.OrdinalIgnoreCase) >= 0
                             || name.IndexOf("what u hear", StringComparison.OrdinalIgnoreCase) >= 0;

                if (loopback) { sys ??= name; }
                else if (name.IndexOf("microphone", StringComparison.OrdinalIgnoreCase) >= 0
                      || name.IndexOf("headset", StringComparison.OrdinalIgnoreCase) >= 0) { mic ??= name; }
            }
        }
        catch { }
        return (mic, sys);
    }

    static int ListDevices()
    {
        if (!RequireFfmpeg(out _)) return 1;
        var (mic, sys) = FindAudioDevices();
        Console.WriteLine("Audio devices RECORD will use:");
        Console.WriteLine($"  mic    : {mic ?? "(none found — narration unavailable)"}");
        Console.WriteLine($"  system : {sys ?? "(none found — install screen-capture-recorder or VB-Cable for game audio)"}");
        return mic == null && sys == null ? 1 : 0;
    }

    // ─── Status / stop ────────────────────────────────────────────────

    static int LivePid()
    {
        try
        {
            if (!File.Exists(PidFile)) return 0;
            if (!int.TryParse(File.ReadAllText(PidFile).Trim(), out int pid)) return 0;
            var p = Process.GetProcessById(pid);
            if (p.HasExited || !p.ProcessName.StartsWith("clibridge", StringComparison.OrdinalIgnoreCase)) return 0;
            return pid;
        }
        catch { return 0; }
    }

    public static bool IsRecording => LivePid() != 0;

    static int Status()
    {
        int pid = LivePid();
        if (pid == 0) { Console.WriteLine("Recorder: idle."); return 1; }
        string target = "";
        try { target = File.ReadAllText(TargetFile).Trim(); } catch { }
        Console.WriteLine($"Recorder: RECORDING (pid {pid})");
        if (!string.IsNullOrEmpty(target)) Console.WriteLine($"Target: {target}");
        Console.WriteLine("Stop with: clibridge4unity RECORD --stop");
        return 0;
    }

    /// <summary>
    /// Ask the recording process to finish. Done via a flag file the recorder polls rather than by
    /// killing it: ffmpeg must write the moov atom on the way out, and a killed ffmpeg leaves an
    /// unplayable mp4 with everything you just recorded stuck inside it.
    /// </summary>
    public static int Stop()
    {
        int pid = LivePid();
        if (pid == 0)
        {
            Console.WriteLine("Recorder: idle — nothing to stop.");
            return 1;
        }
        try { File.WriteAllText(StopFile, DateTime.UtcNow.ToString("o")); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error signalling stop: {ex.Message}");
            return 1;
        }
        Console.WriteLine($"Stop requested (pid {pid}) — finalising the recording…");
        return 0;
    }

    // ─── Record ───────────────────────────────────────────────────────

    static int Record(string outPath, bool full, string audio, int seconds, int fps,
                      bool transcribe, bool noSheet)
    {
        if (!RequireFfmpeg(out string ffmpeg)) return 1;

        int running = LivePid();
        if (running != 0)
        {
            Console.Error.WriteLine($"Already recording (pid {running}). Use RECORD --stop first.");
            return 1;
        }

        int x, y, w, h;
        if (full)
        {
            x = y = 0; w = 0; h = 0;   // gdigrab grabs the whole desktop when no size is given
        }
        else
        {
            Console.Error.WriteLine("Drag the region to record. Esc or right-click cancels.");
            if (!RegionCapture.TrySelectRegion(out x, out y, out w, out h))
            {
                Console.Error.WriteLine("Recording cancelled.");
                return 1;
            }
            // H.264 requires even dimensions in yuv420p; trimming beats a hard encoder failure.
            w -= w % 2; h -= h % 2;
        }

        Directory.CreateDirectory(Dir);
        string dest = string.IsNullOrWhiteSpace(outPath)
            ? Path.Combine(Dir, $"rec_{DateTime.Now:yyyyMMdd_HHmmss}.mp4")
            : Path.GetFullPath(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dest));

        var (mic, sys) = FindAudioDevices();
        var wanted = new List<string>();
        if (audio != "none")
        {
            if ((audio == "mic" || audio == "both") && mic != null) wanted.Add(mic);
            if ((audio == "system" || audio == "both") && sys != null) wanted.Add(sys);
            if (wanted.Count == 0)
                Console.Error.WriteLine($"Warning: no usable audio device for '--audio {audio}' — recording video only.");
        }

        string cmd = BuildFfmpegArgs(x, y, w, h, fps, full, wanted, seconds, dest);

        try { File.Delete(StopFile); } catch { }
        Process ff = null;
        try
        {
            ff = Process.Start(new ProcessStartInfo(ffmpeg, cmd)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,   // 'q' is how ffmpeg is asked to finalise cleanly
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error starting ffmpeg: {ex.Message}");
            return 1;
        }

        File.WriteAllText(PidFile, Process.GetCurrentProcess().Id.ToString());
        File.WriteAllText(TargetFile, dest);

        // Drain stderr on a background thread. ffmpeg is chatty and a full pipe buffer deadlocks
        // the child — the classic way a recording silently stops after ~30 seconds.
        var log = new StringBuilder();
        var drain = new Thread(() =>
        {
            try
            {
                string line;
                while ((line = ff.StandardError.ReadLine()) != null)
                    lock (log) { if (log.Length < 64_000) log.AppendLine(line); }
            }
            catch { }
        }) { IsBackground = true };
        drain.Start();

        string size = full ? "full desktop" : $"{w}x{h} at {x},{y}";
        string audioDesc = wanted.Count == 0 ? "no audio" : string.Join(" + ", wanted);
        Console.Error.WriteLine($"Recording {size} — {audioDesc}");
        Console.Error.WriteLine(seconds > 0
            ? $"Will stop automatically after {seconds}s."
            : "Stop with: clibridge4unity RECORD --stop   (or the tray menu)");

        var started = DateTime.UtcNow;
        try
        {
            while (!ff.HasExited)
            {
                if (File.Exists(StopFile)) { RequestQuit(ff); break; }
                if (seconds > 0 && (DateTime.UtcNow - started).TotalSeconds >= seconds) { RequestQuit(ff); break; }
                Thread.Sleep(200);
            }
            if (!ff.WaitForExit(20000))
            {
                Console.Error.WriteLine("Warning: ffmpeg did not exit cleanly — the mp4 may be truncated.");
                try { ff.Kill(); } catch { }
            }
        }
        finally
        {
            try { File.Delete(PidFile); } catch { }
            try { File.Delete(StopFile); } catch { }
            try { File.Delete(TargetFile); } catch { }
        }

        if (!File.Exists(dest) || new FileInfo(dest).Length < 1024)
        {
            Console.Error.WriteLine("Error: recording produced no usable file. ffmpeg said:");
            lock (log) Console.Error.WriteLine(Tail(log.ToString(), 15));
            return 1;
        }

        double dur = ProbeDuration(dest);
        Console.WriteLine("Recorded");
        Console.WriteLine($"output: {dest}");
        Console.WriteLine($"duration: {dur:0.0}s");

        // The parts an assistant can actually read.
        if (!noSheet)
        {
            string sheet = BuildContactSheet(ffmpeg, dest, dur);
            if (sheet != null) Console.WriteLine($"frames: {sheet}");
        }
        if (transcribe && wanted.Count > 0)
        {
            string txt = Transcribe(ffmpeg, dest);
            if (txt != null) Console.WriteLine($"transcript: {txt}");
        }

        RegionCapture.PublishLatest(dest);
        return 0;
    }

    static void RequestQuit(Process ff)
    {
        try { ff.StandardInput.Write("q"); ff.StandardInput.Flush(); }
        catch { try { ff.Kill(); } catch { } }
    }

    static string BuildFfmpegArgs(int x, int y, int w, int h, int fps, bool full,
                                  List<string> audioDevices, int seconds, string dest)
    {
        var sb = new StringBuilder("-hide_banner -loglevel warning -y ");

        sb.Append($"-f gdigrab -framerate {fps} ");
        if (!full) sb.Append($"-offset_x {x} -offset_y {y} -video_size {w}x{h} ");
        sb.Append("-i desktop ");

        foreach (var d in audioDevices)
            sb.Append($"-f dshow -i audio=\"{d}\" ");

        if (audioDevices.Count == 1)
        {
            sb.Append("-map 0:v -map 1:a ");
        }
        else if (audioDevices.Count > 1)
        {
            // amix rather than two tracks: one mixed track is what a transcriber and a human
            // player both expect, and multi-track mp4 audio is inconsistently supported.
            var inputs = new StringBuilder();
            for (int i = 1; i <= audioDevices.Count; i++) inputs.Append($"[{i}:a]");
            sb.Append($"-filter_complex \"{inputs}amix=inputs={audioDevices.Count}:duration=longest:normalize=0[a]\" ");
            sb.Append("-map 0:v -map \"[a]\" ");
        }
        else
        {
            sb.Append("-map 0:v ");
        }

        sb.Append("-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p ");
        if (audioDevices.Count > 0) sb.Append("-c:a aac -b:a 128k ");
        // +faststart so the moov atom lands up front — the file is seekable the moment it lands.
        sb.Append("-movflags +faststart ");
        if (seconds > 0) sb.Append($"-t {seconds} ");
        sb.Append($"\"{dest}\"");
        return sb.ToString();
    }

    // ─── Post-processing: the bits that carry into a prompt ───────────

    static double ProbeDuration(string path)
    {
        string ffprobe = FindFfprobe();
        if (ffprobe == null) return 0;
        try
        {
            var psi = new ProcessStartInfo(ffprobe,
                $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{path}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);
            return double.TryParse(o, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Sample the recording into one tiled PNG. This is the artefact that makes a video legible to
    /// an assistant at all — 12 evenly-spaced frames read as a sequence, where an mp4 path reads as
    /// nothing. Evenly spaced rather than scene-cut detected: a UI jitter or a one-frame pop is
    /// exactly what scene detection discards as insignificant.
    /// </summary>
    static string BuildContactSheet(string ffmpeg, string video, double duration)
    {
        const int cols = 4, rows = 3, count = cols * rows;
        string sheet = Path.ChangeExtension(video, ".frames.png");

        // Guard the degenerate cases: a sub-second clip cannot yield 12 distinct frames, and
        // fps=0 would make ffmpeg emit nothing at all.
        double rate = duration > 0.5 ? count / duration : 4.0;
        string fpsExpr = rate.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

        string args = $"-hide_banner -loglevel error -y -i \"{video}\" " +
                      $"-vf \"fps={fpsExpr},scale=480:-2:flags=lanczos,tile={cols}x{rows}\" " +
                      $"-frames:v 1 \"{sheet}\"";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            });
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(120000);
            if (File.Exists(sheet)) return sheet;
            Console.Error.WriteLine($"Warning: contact sheet failed. {Tail(err, 4)}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"Warning: contact sheet failed: {ex.Message}"); }
        return null;
    }

    /// <summary>
    /// Transcribe with ffmpeg's built-in whisper filter (this build is compiled --enable-whisper,
    /// so there is no separate whisper.cpp to install — only the model weights).
    /// </summary>
    static string Transcribe(string ffmpeg, string video)
    {
        string model = Path.Combine(WhisperDir, WhisperModel);
        if (!File.Exists(model) && !TryFetchModel(model)) return null;

        string outTxt = Path.ChangeExtension(video, ".transcript.txt");
        // Filter args are colon-separated, so the model path must have its colons and backslashes
        // escaped or the filtergraph parser eats the drive letter.
        string esc = model.Replace("\\", "/").Replace(":", "\\:");
        string dst = outTxt.Replace("\\", "/").Replace(":", "\\:");

        string args = $"-hide_banner -loglevel error -y -i \"{video}\" -vn " +
                      $"-af \"whisper=model={esc}:language=en:format=srt:destination={dst}\" -f null -";
        try
        {
            Console.Error.WriteLine("Transcribing…");
            using var p = Process.Start(new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            });
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(600000);
            if (File.Exists(outTxt) && new FileInfo(outTxt).Length > 0) return outTxt;
            Console.Error.WriteLine($"Warning: transcription produced nothing. {Tail(err, 4)}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"Warning: transcription failed: {ex.Message}"); }
        return null;
    }

    static bool TryFetchModel(string dest)
    {
        Console.Error.WriteLine($"Whisper model not present. Downloading {WhisperModel} (~148 MB) to {WhisperDir}");
        Console.Error.WriteLine("(one-off — skip transcription with plain RECORD if you would rather not)");
        try
        {
            Directory.CreateDirectory(WhisperDir);
            string part = dest + ".part";
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
            using (var stream = http.GetStreamAsync(WhisperUrl).GetAwaiter().GetResult())
            using (var fs = File.Create(part))
                stream.CopyTo(fs);
            // .part + rename so an interrupted download never leaves a truncated model that
            // fails later as a confusing filter error.
            File.Move(part, dest, true);
            Console.Error.WriteLine("Model downloaded.");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not download the whisper model: {ex.Message}");
            return false;
        }
    }

    static string Tail(string s, int lines)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var all = s.TrimEnd().Split('\n');
        int start = Math.Max(0, all.Length - lines);
        return string.Join("\n", all[start..]).Trim();
    }

    // ─── Used by the tray menu ────────────────────────────────────────

    /// <summary>Start a recording in a detached child so the tray's message loop keeps pumping.</summary>
    public static void StartDetached(string extraArgs)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath, $"RECORD {extraArgs}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"Could not start recording: {ex.Message}"); }
    }
}
