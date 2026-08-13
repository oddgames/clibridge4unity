using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEditor.Profiling;
using UnityEditorInternal;

namespace clibridge4unity
{
    /// <summary>
    /// Frame-cost ranking and marker aggregation over whatever is currently in the Profiler's
    /// frame buffer. The source is deliberately not distinguished: ProfilerDriver serves a loaded
    /// .data capture, a live play-mode session, and a paused game through the same frame views, so
    /// PROFILE load + PROFILE frames/top/breakdown read identically in all three cases.
    ///
    /// Everything here is main-thread-only (the frame views are editor APIs) and therefore runs
    /// under an explicit wall-clock budget: a deep-profiled capture holds ~250k samples per frame,
    /// and blocking the editor for minutes to rank them all is never worth it. Partial results with
    /// an honest "scanned N of M" beat a complete answer that freezes Unity.
    /// </summary>
    internal static class ProfilerAnalysis
    {
        /// <summary>Main thread is index 0 in every frame view Unity produces.</summary>
        internal const int MainThreadIndex = 0;

        /// <summary>Per-marker visitor: name, self ms, total ms, calls, GC bytes.</summary>
        internal delegate void MarkerVisit(string name, float selfMs, float totalMs, float calls, float gcBytes);

        internal struct FrameCost
        {
            public int Frame;
            public float Ms;
        }

        internal sealed class MarkerStat
        {
            public string Name;
            public double SelfMs;
            public double TotalMs;
            public double Calls;
            public double GcBytes;
            public int FramesSeen;

            /// <summary>Microseconds per call — separates "slow function" from "called far too often".</summary>
            public double UsPerCall(int frames)
            {
                double cps = Calls / Math.Max(1, frames);
                return cps > 0 ? (SelfMs / Math.Max(1, frames)) * 1000.0 / cps : 0;
            }
        }

        internal sealed class Scan
        {
            public List<FrameCost> Frames = new List<FrameCost>();
            public int FirstFrame, LastFrame, AvailableFrames;
            public bool Truncated;
            public float Mean, Median, P95, P99, Max;

            /// <summary>Frames at or below 1.5x median — the steady state an opinion should rest on.</summary>
            public List<int> Steady =>
                Frames.Where(f => f.Ms <= Median * 1.5f).Select(f => f.Frame).ToList();

            /// <summary>Frames at or above 2x median, worst first — hitches, whose cause is usually distinct.</summary>
            public List<int> Spikes =>
                Frames.Where(f => f.Ms >= Median * 2f).OrderByDescending(f => f.Ms).Select(f => f.Frame).ToList();

            public float MsOf(int frame)
            {
                foreach (var f in Frames) if (f.Frame == frame) return f.Ms;
                return 0f;
            }
        }

        /// <summary>
        /// Per-frame wall time for the requested thread. Uses the raw view: it carries frameTimeMs
        /// without building the hierarchy tree, which is what makes a full-range sweep affordable.
        /// </summary>
        internal static Scan ScanFrames(int threadIndex, int budgetMs, int fromFrame = -1, int toFrame = -1)
        {
            var sw = Stopwatch.StartNew();
            var scan = new Scan();

            int first = ProfilerDriver.firstFrameIndex;
            int last = ProfilerDriver.lastFrameIndex;
            if (first < 0 || last < first) return scan;

            if (fromFrame >= 0) first = Math.Max(first, fromFrame);
            if (toFrame >= 0) last = Math.Min(last, toFrame);

            scan.FirstFrame = first;
            scan.LastFrame = last;
            scan.AvailableFrames = last - first + 1;

            for (int f = first; f <= last; f++)
            {
                if (sw.ElapsedMilliseconds > budgetMs) { scan.Truncated = true; break; }
                try
                {
                    using (var raw = ProfilerDriver.GetRawFrameDataView(f, threadIndex))
                    {
                        if (raw != null && raw.valid)
                            scan.Frames.Add(new FrameCost { Frame = f, Ms = raw.frameTimeMs });
                    }
                }
                catch { /* frames can be evicted mid-scan; skip */ }
            }

            if (scan.Frames.Count > 0)
            {
                var sorted = scan.Frames.Select(x => x.Ms).OrderBy(v => v).ToList();
                scan.Mean = sorted.Average();
                scan.Median = sorted[sorted.Count / 2];
                scan.P95 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95f))];
                scan.P99 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.99f))];
                scan.Max = sorted[sorted.Count - 1];
            }
            return scan;
        }

        internal static HierarchyFrameDataView OpenHierarchy(int frame, int threadIndex)
        {
            return ProfilerDriver.GetHierarchyFrameDataView(
                frame, threadIndex,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                HierarchyFrameDataView.columnSelfTime, false);
        }

        /// <summary>Depth-first walk of a hierarchy frame view. Guarded against pathological trees.</summary>
        internal static void WalkFrame(HierarchyFrameDataView h, MarkerVisit visit)
        {
            var stack = new Stack<int>();
            stack.Push(h.GetRootItemID());
            var kids = new List<int>();
            int guard = 0;
            while (stack.Count > 0 && guard++ < 200000)
            {
                int id = stack.Pop();
                visit(
                    h.GetItemName(id),
                    h.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnSelfTime),
                    h.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime),
                    h.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnCalls),
                    h.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnGcMemory));
                kids.Clear();
                h.GetItemChildren(id, kids);
                for (int i = 0; i < kids.Count; i++) stack.Push(kids[i]);
            }
        }

        /// <summary>Heaviest single marker (by self time) in one frame — the "why was this frame slow" hint.</summary>
        internal static string TopMarkerInFrame(int frame, int threadIndex)
        {
            try
            {
                using (var h = OpenHierarchy(frame, threadIndex))
                {
                    if (h == null || !h.valid) return "";
                    string best = ""; float bestSelf = 0f;
                    WalkFrame(h, (name, self, tot, calls, gc) =>
                    {
                        if (self > bestSelf) { bestSelf = self; best = name; }
                    });
                    return string.IsNullOrEmpty(best) ? "" : $"{best} ({bestSelf:F2}ms self)";
                }
            }
            catch (Exception e) { return "err: " + e.GetType().Name; }
        }

        /// <summary>
        /// Aggregate self/total/calls/GC per marker across a set of frames. Frames are supplied by
        /// the caller so this serves both "hot methods overall" (an even stride across the range)
        /// and "what made the spikes expensive" (the worst frames only).
        /// </summary>
        internal static List<MarkerStat> AggregateMarkers(
            IEnumerable<int> frames, int threadIndex, int budgetMs, out int analyzed)
        {
            var sw = Stopwatch.StartNew();
            var acc = new Dictionary<string, MarkerStat>(StringComparer.Ordinal);
            analyzed = 0;

            foreach (int f in frames)
            {
                if (sw.ElapsedMilliseconds > budgetMs) break;
                try
                {
                    using (var h = OpenHierarchy(f, threadIndex))
                    {
                        if (h == null || !h.valid) continue;
                        analyzed++;
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        WalkFrame(h, (name, self, tot, calls, gc) =>
                        {
                            if (string.IsNullOrEmpty(name)) return;
                            MarkerStat m;
                            if (!acc.TryGetValue(name, out m))
                            {
                                m = new MarkerStat { Name = name };
                                acc[name] = m;
                            }
                            m.SelfMs += self;
                            m.TotalMs += tot;
                            m.Calls += calls;
                            m.GcBytes += gc;
                            if (seen.Add(name)) m.FramesSeen++;
                        });
                    }
                }
                catch { /* skip unreadable frame */ }
            }

            return acc.Values.OrderByDescending(m => m.SelfMs).ToList();
        }

        /// <summary>
        /// Top-level split of the frame (PlayerLoop / EditorLoop / Profiler overhead / ...). This is
        /// the number that decides whether a capture is even worth reading: an in-editor capture
        /// where EditorLoop owns a third of the frame cannot be read as game cost.
        /// </summary>
        internal static List<KeyValuePair<string, double>> RootBreakdown(
            IEnumerable<int> frames, int threadIndex, out double avgFrameMs, out int analyzed)
        {
            var acc = new Dictionary<string, double>(StringComparer.Ordinal);
            double totalMs = 0;
            analyzed = 0;

            foreach (int f in frames)
            {
                try
                {
                    using (var h = OpenHierarchy(f, threadIndex))
                    {
                        if (h == null || !h.valid) continue;
                        analyzed++;
                        totalMs += h.frameTimeMs;
                        var kids = new List<int>();
                        h.GetItemChildren(h.GetRootItemID(), kids);
                        foreach (int k in kids)
                        {
                            string nm = h.GetItemName(k);
                            double t = h.GetItemColumnDataAsFloat(k, HierarchyFrameDataView.columnTotalTime);
                            double cur; acc.TryGetValue(nm, out cur); acc[nm] = cur + t;
                        }
                    }
                }
                catch { }
            }

            avgFrameMs = analyzed > 0 ? totalMs / analyzed : 0;
            int n = Math.Max(1, analyzed);
            return acc.Select(kv => new KeyValuePair<string, double>(kv.Key, kv.Value / n))
                      .OrderByDescending(kv => kv.Value).ToList();
        }

        /// <summary>Thread names available in a frame. Probes upward until the view stops being valid.</summary>
        internal static List<string> ListThreads(int frame, int max = 64, int minSamples = 0)
        {
            var names = new List<string>();
            for (int t = 0; t < max; t++)
            {
                try
                {
                    using (var raw = ProfilerDriver.GetRawFrameDataView(frame, t))
                    {
                        if (raw == null || !raw.valid) break;
                        if (raw.sampleCount < minSamples) continue;
                        string group = raw.threadGroupName;
                        names.Add($"{t}: {(string.IsNullOrEmpty(group) ? "" : group + ".")}{raw.threadName}" +
                                  $"  {raw.frameTimeMs:F2}ms  samples={raw.sampleCount}");
                    }
                }
                catch { break; }
            }
            return names;
        }

        /// <summary>Evenly-spaced subset, so a ranking reflects the whole range rather than its head.</summary>
        internal static List<int> EvenSample(List<int> src, int count)
        {
            if (src == null || src.Count == 0) return new List<int>();
            if (src.Count <= count) return new List<int>(src);
            var outp = new List<int>(count);
            int stride = Math.Max(1, src.Count / count);
            for (int i = 0; i < src.Count && outp.Count < count; i += stride) outp.Add(src[i]);
            return outp;
        }

        /// <summary>
        /// Markers whose cost is an artifact of measuring or of running inside the editor, rather
        /// than of the game. Surfaced explicitly so a capture is not misread as "EditorLoop is the
        /// bottleneck" — in a player build neither EditorLoop nor Profiler.* exists.
        /// </summary>
        internal static bool IsOverheadMarker(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.StartsWith("Profiler.", StringComparison.Ordinal)
                || name == "EditorLoop"
                || name == "Mono.JIT"
                || name == "EditorOverhead"
                || name.StartsWith("ProfilerRecorder", StringComparison.Ordinal);
        }

        // ---- Re-grouping ------------------------------------------------------------------
        // Deep-profiled marker names carry their own structure:
        //   "MTD2.WheelDust.Vehicle.dll!MTD2.WheelDust.Vehicle::TruckDustEmitter.Update() [Invoke]"
        // Native markers instead use a dotted prefix ("Physics.Simulate", "SRPBatcher.Flush").
        // Collapsing either into a coarser key turns a 2000-row leaf ranking into a readable
        // "which system / which assembly owns this frame" answer.

        /// <summary>Assembly part of a managed marker, or null for a native/builtin marker.</summary>
        internal static string AssemblyOf(string name)
        {
            int bang = name.IndexOf(".dll!", StringComparison.Ordinal);
            return bang < 0 ? null : name.Substring(0, bang);
        }

        /// <summary>Portion after the assembly prefix — "Namespace::Class.Method()" or the raw name.</summary>
        private static string AfterAssembly(string name)
        {
            int bang = name.IndexOf('!');
            return bang < 0 ? name : name.Substring(bang + 1);
        }

        /// <summary>
        /// Collapse a marker name to a grouping key. Modes: assembly, namespace, class, prefix.
        /// Unmatched shapes fall back to the whole name so nothing silently vanishes from a total.
        /// </summary>
        internal static string GroupKeyFor(string name, string mode)
        {
            if (string.IsNullOrEmpty(name)) return name;

            string asm = AssemblyOf(name);
            string rest = AfterAssembly(name);
            int colons = rest.IndexOf("::", StringComparison.Ordinal);

            switch (mode)
            {
                case "assembly":
                    return asm ?? "(native/builtin)";

                case "namespace":
                    if (colons >= 0) return rest.Substring(0, colons);
                    return asm ?? PrefixOf(name);

                case "class":
                {
                    // "Namespace::Class.Method()" -> "Namespace::Class"
                    string tail = colons >= 0 ? rest.Substring(colons + 2) : rest;
                    int paren = tail.IndexOf('(');
                    if (paren >= 0) tail = tail.Substring(0, paren);
                    int lastDot = tail.LastIndexOf('.');
                    string cls = lastDot > 0 ? tail.Substring(0, lastDot) : tail;
                    if (colons >= 0) return rest.Substring(0, colons) + "::" + cls;
                    return string.IsNullOrEmpty(cls) ? name : cls;
                }

                default: // "prefix" — first dotted segment, which is how native markers are namespaced
                    return asm ?? PrefixOf(name);
            }
        }

        private static string PrefixOf(string name)
        {
            int dot = name.IndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        /// <summary>Fold a marker ranking into coarser buckets, summing every component metric.</summary>
        internal static List<MarkerStat> Regroup(IEnumerable<MarkerStat> stats, string mode, string filter)
        {
            var acc = new Dictionary<string, MarkerStat>(StringComparer.Ordinal);
            foreach (var s in stats)
            {
                if (!string.IsNullOrEmpty(filter) &&
                    s.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                string key = GroupKeyFor(s.Name, mode);
                MarkerStat g;
                if (!acc.TryGetValue(key, out g)) { g = new MarkerStat { Name = key }; acc[key] = g; }
                g.SelfMs += s.SelfMs;
                g.TotalMs += s.TotalMs;
                g.Calls += s.Calls;
                g.GcBytes += s.GcBytes;
                g.FramesSeen = Math.Max(g.FramesSeen, s.FramesSeen);
            }
            return acc.Values.OrderByDescending(m => m.SelfMs).ToList();
        }

        /// <summary>
        /// Render the call tree under the first nodes whose name matches <paramref name="match"/>
        /// (or from the root when empty). Single-frame by design: a tree is a shape, and averaging
        /// shapes across frames invents parents that never existed. Pick the frame deliberately.
        /// </summary>
        internal static void AppendSubtree(HierarchyFrameDataView h, string match, int maxDepth,
            float minMs, StringBuilder sb, int maxRows = 200)
        {
            int rows = 0;
            var roots = new List<int>();

            if (string.IsNullOrEmpty(match))
            {
                h.GetItemChildren(h.GetRootItemID(), roots);
            }
            else
            {
                WalkIds(h, id =>
                {
                    if (roots.Count < 8 &&
                        h.GetItemName(id).IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                        roots.Add(id);
                });
                if (roots.Count == 0)
                {
                    sb.AppendLine($"  (no marker matching '{match}' in this frame)");
                    return;
                }
            }

            foreach (int r in roots) Emit(h, r, 0, maxDepth, minMs, sb, ref rows, maxRows);
            if (rows >= maxRows) sb.AppendLine($"  ... truncated at {maxRows} rows (raise depth:/min: to narrow)");
        }

        private static void Emit(HierarchyFrameDataView h, int id, int depth, int maxDepth,
            float minMs, StringBuilder sb, ref int rows, int maxRows)
        {
            if (rows >= maxRows) return;
            float total = h.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime);
            if (total < minMs) return;

            float self = h.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnSelfTime);
            float calls = h.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnCalls);
            float gc = h.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnGcMemory);

            sb.AppendLine($"{total,9:F2}ms {self,8:F2}ms {calls,7:F0} {gc,9:F0}B  " +
                          new string(' ', depth * 2) + h.GetItemName(id));
            rows++;

            if (depth >= maxDepth) return;
            var kids = new List<int>();
            h.GetItemChildren(id, kids);
            // Heaviest first: a truncated tree should keep the part that matters.
            kids.Sort((a, b) => h.GetItemColumnDataAsFloat(b, HierarchyFrameDataView.columnTotalTime)
                        .CompareTo(h.GetItemColumnDataAsFloat(a, HierarchyFrameDataView.columnTotalTime)));
            foreach (int k in kids) Emit(h, k, depth + 1, maxDepth, minMs, sb, ref rows, maxRows);
        }

        private static void WalkIds(HierarchyFrameDataView h, Action<int> visit)
        {
            WalkIdsWithParent(h, (id, parent) => visit(id));
        }

        /// <summary>
        /// Walk carrying each node's parent id. HierarchyFrameDataView exposes no parent lookup, so
        /// the traversal supplies it — which is free here, since we descend through the parent anyway.
        /// </summary>
        private static void WalkIdsWithParent(HierarchyFrameDataView h, Action<int, int> visit)
        {
            var stack = new Stack<KeyValuePair<int, int>>();
            stack.Push(new KeyValuePair<int, int>(h.GetRootItemID(), -1));
            var kids = new List<int>();
            int guard = 0;
            while (stack.Count > 0 && guard++ < 200000)
            {
                var cur = stack.Pop();
                visit(cur.Key, cur.Value);
                kids.Clear();
                h.GetItemChildren(cur.Key, kids);
                for (int i = 0; i < kids.Count; i++)
                    stack.Push(new KeyValuePair<int, int>(kids[i], cur.Key));
            }
        }

        /// <summary>
        /// Who pays for a marker: sums the marker's cost grouped by its immediate parent, across
        /// frames. Answers "290 SRPBatcher.Flush calls — issued from where?", which a top-down tree
        /// cannot when the marker appears under many parents.
        /// </summary>
        internal static List<MarkerStat> FindCallers(IEnumerable<int> frames, int threadIndex,
            string markerMatch, int budgetMs, out int analyzed)
        {
            var sw = Stopwatch.StartNew();
            var acc = new Dictionary<string, MarkerStat>(StringComparer.Ordinal);
            analyzed = 0;

            foreach (int f in frames)
            {
                if (sw.ElapsedMilliseconds > budgetMs) break;
                try
                {
                    using (var h = OpenHierarchy(f, threadIndex))
                    {
                        if (h == null || !h.valid) continue;
                        analyzed++;
                        WalkIdsWithParent(h, (id, parent) =>
                        {
                            string nm = h.GetItemName(id);
                            if (string.IsNullOrEmpty(nm) ||
                                nm.IndexOf(markerMatch, StringComparison.OrdinalIgnoreCase) < 0) return;

                            string key = parent < 0 ? "(frame root)" : h.GetItemName(parent);
                            MarkerStat m;
                            if (!acc.TryGetValue(key, out m)) { m = new MarkerStat { Name = key }; acc[key] = m; }
                            m.SelfMs += h.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnSelfTime);
                            m.TotalMs += h.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime);
                            m.Calls += h.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnCalls);
                            m.GcBytes += h.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnGcMemory);
                        });
                    }
                }
                catch { }
            }
            return acc.Values.OrderByDescending(m => m.TotalMs).ToList();
        }

        internal static void AppendScanHeader(StringBuilder sb, Scan scan, int threadIndex)
        {
            sb.AppendLine($"frames {scan.FirstFrame}..{scan.LastFrame}  " +
                          $"({scan.Frames.Count} scanned of {scan.AvailableFrames}" +
                          $"{(scan.Truncated ? ", truncated by time budget" : "")}, thread {threadIndex})");
            if (scan.Frames.Count == 0) return;
            sb.AppendLine($"mean {scan.Mean:F2}ms | median {scan.Median:F2}ms " +
                          $"({(scan.Median > 0 ? 1000f / scan.Median : 0):F0}fps) | " +
                          $"p95 {scan.P95:F2}ms | p99 {scan.P99:F2}ms | max {scan.Max:F2}ms");
        }

        /// <summary>
        /// Capture provenance. Every report leads with this: deep profiling inflates absolute times
        /// (per-call instrumentation), so a ranking taken under it is only trustworthy as a ratio.
        /// </summary>
        internal static void AppendCaptureFlags(StringBuilder sb)
        {
            sb.AppendLine($"deepProfiling: {ProfilerDriver.deepProfiling}" +
                          (ProfilerDriver.deepProfiling
                              ? "  <- per-call instrumentation: absolute ms are inflated, compare ratios not absolutes"
                              : "  <- marker-level only: no per-method rows, use 'PROFILE deep on' to get them"));
            sb.AppendLine($"profileEditor: {ProfilerDriver.profileEditor}   " +
                          $"enabled: {ProfilerDriver.enabled}   connected: {ProfilerDriver.connectedProfiler}");
        }
    }
}
