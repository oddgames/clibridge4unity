using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace clibridge4unity;

/// <summary>
/// Managed-heap crawl for MEMSNAP — the part the native views cannot answer: "N instances of Foo,
/// M bytes, held by what".
///
/// This is the Memory Profiler package's Crawler.cs algorithm, not a heuristic scan:
///   roots     = every GC handle target + every reference-typed static field (TypeDescriptions_StaticFieldBytes)
///   an object = its first pointer-sized word is the Il2CppClass*/MonoClass*, matched against
///               TypeDescriptions_TypeInfoAddress; the type's declared Size is the instance size
///   arrays    = header (arrayHeaderSize) + length (Int32 at arraySizeOffsetInHeader) × element size,
///               element type = BaseOrElementTypeIndex; TypeFlags.kArray marks them
///   strings   = objectHeaderSize + Int32 length + UTF-16 chars + terminator
///   fields    = walked through the base-class chain (FieldIndices only lists a type's OWN fields);
///               a value-type field's inner offsets are stored as-if-boxed, so they are rebased by
///               objectHeaderSize, exactly as the package does
/// Only what is reachable from a root is counted — the same set the profiler's "Managed Objects"
/// tab shows. Heap bytes not reached are reported as unreachable (garbage awaiting collection,
/// free-list slack, or objects only referenced from a thread stack).
/// </summary>
static partial class MemorySnapshot
{
    const uint TypeFlagValueType = 1u << 0;
    const uint TypeFlagArray = 1u << 1;
    const uint TypeFlagArrayRankMask = 0xFFFF0000u;

    sealed class HeapSection
    {
        public ulong Start;
        public byte[] Bytes;
        public ulong End => Start + (ulong)Bytes.LongLength;
    }

    sealed class ManagedModel
    {
        public int PointerSize, ObjectHeaderSize, ArrayHeaderSize, ArrayBoundsOffset, ArraySizeOffset, AllocationGranularity;
        public uint[] TypeFlags = Array.Empty<uint>();
        public string[] TypeNames = Array.Empty<string>();
        public string[] TypeAssemblies = Array.Empty<string>();
        public int[][] TypeFieldIndices = Array.Empty<int[]>();
        public byte[][] TypeStaticBytes = Array.Empty<byte[]>();
        public int[] TypeBaseOrElement = Array.Empty<int>();
        public int[] TypeSizes = Array.Empty<int>();
        public ulong[] TypeInfoAddresses = Array.Empty<ulong>();
        public int[] FieldOffsets = Array.Empty<int>();
        public int[] FieldTypeIndices = Array.Empty<int>();
        public string[] FieldNames = Array.Empty<string>();
        public bool[] FieldIsStatic = Array.Empty<bool>();
        public ulong[] GcHandleTargets = Array.Empty<ulong>();
        public List<HeapSection> Sections = new();
        public Dictionary<ulong, int> TypeByInfoAddress = new();
        public int StringTypeIndex = -1;

        public bool IsValueType(int t) => (TypeFlags[t] & TypeFlagValueType) != 0;
        public bool IsArray(int t) => (TypeFlags[t] & TypeFlagArray) != 0;
        public int ArrayRank(int t) => (int)((TypeFlags[t] & TypeFlagArrayRankMask) >> 16);
    }

    sealed class ManagedResult
    {
        public ulong HeapBytes, ReachableBytes;
        public long ObjectCount;
        public int Roots;
        /// <summary>type name → (bytes, count)</summary>
        public Dictionary<string, (long bytes, int count)> ByType = new();
        /// <summary>largest single objects: (bytes, description)</summary>
        public List<(long bytes, string desc)> Largest = new();
        /// <summary>type name → referrer path ("Owner.field" / "Owner[]" / "static Owner.field" / "gc handle") → reference count</summary>
        public Dictionary<string, Dictionary<string, int>> Referrers = new();
        public long StringCount; public ulong StringBytes;
        public List<string> Warnings = new();
    }

    static ManagedModel LoadManagedModel(SnapFile f)
    {
        if (!f.Has(EntryType.TypeDescriptions_TypeInfoAddress) || !f.Has(EntryType.ManagedHeapSections_Bytes))
            throw new InvalidDataException("no managed chapters in this capture (taken without Managed Objects)");

        var m = new ManagedModel();
        byte[] vm = f.ReadAll(EntryType.Metadata_VirtualMachineInformation);
        if (vm.Length < 24) throw new InvalidDataException("virtual machine information chapter is missing or short");
        m.PointerSize = BitConverter.ToInt32(vm, 0);
        m.ObjectHeaderSize = BitConverter.ToInt32(vm, 4);
        m.ArrayHeaderSize = BitConverter.ToInt32(vm, 8);
        m.ArrayBoundsOffset = BitConverter.ToInt32(vm, 12);
        m.ArraySizeOffset = BitConverter.ToInt32(vm, 16);
        m.AllocationGranularity = BitConverter.ToInt32(vm, 20);
        if (m.PointerSize != 4 && m.PointerSize != 8) throw new InvalidDataException($"unexpected pointer size {m.PointerSize}");

        m.TypeFlags = f.ReadUInts(EntryType.TypeDescriptions_Flags);
        m.TypeNames = f.ReadStrings(EntryType.TypeDescriptions_Name);
        m.TypeAssemblies = f.ReadStrings(EntryType.TypeDescriptions_Assembly);
        m.TypeBaseOrElement = f.ReadInts(EntryType.TypeDescriptions_BaseOrElementTypeIndex);
        m.TypeSizes = f.ReadInts(EntryType.TypeDescriptions_Size);
        m.TypeInfoAddresses = f.ReadULongs(EntryType.TypeDescriptions_TypeInfoAddress);
        m.FieldOffsets = f.ReadInts(EntryType.FieldDescriptions_Offset);
        m.FieldTypeIndices = f.ReadInts(EntryType.FieldDescriptions_TypeIndex);
        m.FieldNames = f.ReadStrings(EntryType.FieldDescriptions_Name);
        m.FieldIsStatic = f.ReadAll(EntryType.FieldDescriptions_IsStatic).Select(b => b != 0).ToArray();
        m.GcHandleTargets = f.ReadULongs(EntryType.GCHandles_Target);

        int typeCount = m.TypeNames.Length;
        m.TypeFieldIndices = new int[typeCount][];
        m.TypeStaticBytes = new byte[typeCount][];
        for (int i = 0; i < typeCount; i++)
        {
            byte[] fi = f.ReadElement(EntryType.TypeDescriptions_FieldIndices, i);
            var idx = new int[fi.Length / 4];
            for (int k = 0; k < idx.Length; k++) idx[k] = BitConverter.ToInt32(fi, k * 4);
            m.TypeFieldIndices[i] = idx;
            m.TypeStaticBytes[i] = f.ReadElement(EntryType.TypeDescriptions_StaticFieldBytes, i);
            if (m.TypeInfoAddresses.Length > i && m.TypeInfoAddresses[i] != 0)
                m.TypeByInfoAddress[m.TypeInfoAddresses[i]] = i;
            if (m.StringTypeIndex < 0 && m.TypeNames[i] == "System.String") m.StringTypeIndex = i;
        }

        var starts = f.ReadULongs(EntryType.ManagedHeapSections_StartAddress);
        uint sectionCount = f.Count(EntryType.ManagedHeapSections_Bytes);
        ulong total = f.TotalBytes(EntryType.ManagedHeapSections_Bytes);
        if (total > 1536UL << 20)
            throw new InvalidDataException($"managed heap is {Fmt(total)} — an Editor capture; crawl a player capture instead");
        for (uint i = 0; i < sectionCount && i < starts.Length; i++)
            m.Sections.Add(new HeapSection { Start = starts[i], Bytes = f.ReadElement(EntryType.ManagedHeapSections_Bytes, i) });
        m.Sections.Sort((a, b) => a.Start.CompareTo(b.Start));
        return m;
    }

    /// <summary>Locate an address inside the heap; -1 when it is outside every section.</summary>
    static int FindSection(List<HeapSection> s, ulong addr)
    {
        int lo = 0, hi = s.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (addr < s[mid].Start) hi = mid - 1;
            else if (addr >= s[mid].End) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    static ManagedResult CrawlManaged(SnapFile f)
    {
        var m = LoadManagedModel(f);
        var r = new ManagedResult { HeapBytes = (ulong)m.Sections.Sum(s => s.Bytes.LongLength) };
        var visited = new HashSet<ulong>();
        var queue = new Stack<ulong>();
        var largest = new List<(long bytes, string desc)>();
        // First discovery edge per object, so a large object can be traced back a few hops.
        var via = new Dictionary<ulong, (ulong parent, string path)>();
        ulong current = 0;
        int ptr = m.PointerSize;

        ulong ReadPointer(HeapSection s, ulong addr)
        {
            long off = (long)(addr - s.Start);
            if (off < 0 || off + ptr > s.Bytes.LongLength) return 0;
            return ptr == 8 ? BitConverter.ToUInt64(s.Bytes, (int)off) : BitConverter.ToUInt32(s.Bytes, (int)off);
        }
        int ReadInt32(HeapSection s, ulong addr)
        {
            long off = (long)(addr - s.Start);
            if (off < 0 || off + 4 > s.Bytes.LongLength) return 0;
            return BitConverter.ToInt32(s.Bytes, (int)off);
        }

        void Refer(ulong target, string path)
        {
            if (target == 0) return;
            int si = FindSection(m.Sections, target);
            if (si < 0) return;
            // Only count references to real objects: the type pointer must resolve.
            if (!m.TypeByInfoAddress.TryGetValue(ReadPointer(m.Sections[si], target), out int ti)) return;
            string tn = m.TypeNames[ti];
            if (!r.Referrers.TryGetValue(tn, out var d)) r.Referrers[tn] = d = new Dictionary<string, int>();
            d.TryGetValue(path, out int c); d[path] = c + 1;
            if (visited.Add(target)) { queue.Push(target); via[target] = (current, path); }
        }

        string Chain(ulong addr)
        {
            var sb = new StringBuilder();
            for (int hop = 0; hop < 4 && via.TryGetValue(addr, out var v); hop++)
            {
                sb.Append(" ← ").Append(v.path);
                if (v.parent == 0) break;
                addr = v.parent;
            }
            return sb.ToString();
        }

        // Walks the reference-typed fields of a value found at (section, baseAddr): a class instance,
        // an array element, or a struct embedded in either. Struct field offsets are as-if-boxed.
        void CrawlFields(HeapSection s, ulong baseAddr, int typeIndex, bool boxedOffsets, string owner, int depth)
        {
            if (depth > 8) return;
            int t = typeIndex;
            while (t >= 0 && t < m.TypeNames.Length)
            {
                foreach (int fi in m.TypeFieldIndices[t])
                {
                    if (fi < 0 || fi >= m.FieldOffsets.Length || m.FieldIsStatic[fi]) continue;
                    int ft = m.FieldTypeIndices[fi];
                    if (ft < 0 || ft >= m.TypeNames.Length) continue;
                    int off = m.FieldOffsets[fi] - (boxedOffsets ? m.ObjectHeaderSize : 0);
                    if (off < 0) continue;
                    ulong at = baseAddr + (ulong)off;
                    if (m.IsValueType(ft))
                    {
                        if (ft == t || ft == typeIndex) continue;                      // primitives contain themselves
                        if (m.TypeFieldIndices[ft].Length == 0) continue;
                        CrawlFields(s, at, ft, true, owner, depth + 1);
                    }
                    else
                        Refer(ReadPointer(s, at), owner + "." + m.FieldNames[fi]);
                }
                if (m.IsValueType(t)) break;                                          // structs have no base chain worth walking
                t = m.TypeBaseOrElement[t];
                if (t == typeIndex) break;
            }
        }

        // Roots: GC handles, then every static field of every type.
        foreach (ulong target in m.GcHandleTargets) Refer(target, "gc handle");
        for (int ti = 0; ti < m.TypeNames.Length; ti++)
        {
            byte[] st = m.TypeStaticBytes[ti];
            if (st == null || st.Length == 0) continue;
            var sec = new HeapSection { Start = 0, Bytes = st };
            string owner = "static " + m.TypeNames[ti];
            foreach (int fi in m.TypeFieldIndices[ti])
            {
                if (fi < 0 || fi >= m.FieldOffsets.Length || !m.FieldIsStatic[fi]) continue;
                int ft = m.FieldTypeIndices[fi];
                if (ft < 0 || ft >= m.TypeNames.Length) continue;
                int off = m.FieldOffsets[fi];
                if (off < 0 || off + ptr > st.Length) continue;
                if (m.IsValueType(ft))
                {
                    if (ft != ti && m.TypeFieldIndices[ft].Length > 0) CrawlFields(sec, (ulong)off, ft, true, owner + "." + m.FieldNames[fi], 1);
                }
                else
                    Refer(ReadPointer(sec, (ulong)off), owner + "." + m.FieldNames[fi]);
            }
        }
        r.Roots = visited.Count;

        while (queue.Count > 0)
        {
            ulong addr = queue.Pop();
            current = addr;
            int si = FindSection(m.Sections, addr);
            if (si < 0) continue;
            var s = m.Sections[si];
            if (!m.TypeByInfoAddress.TryGetValue(ReadPointer(s, addr), out int ti)) continue;
            string tn = m.TypeNames[ti];
            long size;

            if (m.IsArray(ti))
            {
                int et = m.TypeBaseOrElement[ti];
                int len = ReadInt32(s, addr + (ulong)m.ArraySizeOffset);
                if (len < 0 || et < 0 || et >= m.TypeNames.Length) continue;
                int elem = m.IsValueType(et) ? Math.Max(1, m.TypeSizes[et]) : ptr;
                size = m.ArrayHeaderSize + (long)len * elem;
                if ((ulong)size > s.End - addr) size = (long)(s.End - addr);
                if (m.IsValueType(et))
                {
                    if (m.TypeFieldIndices[et].Length > 0 && et != ti && !IsPrimitiveName(m.TypeNames[et]))
                    {
                        long cap = Math.Min(len, 1 << 20);
                        for (long i = 0; i < cap; i++)
                            CrawlFields(s, addr + (ulong)m.ArrayHeaderSize + (ulong)(i * elem), et, true, tn, 1);
                    }
                }
                else
                {
                    string path = m.TypeNames[et] + "[]";
                    long cap = Math.Min(len, 1 << 22);
                    for (long i = 0; i < cap; i++)
                        Refer(ReadPointer(s, addr + (ulong)m.ArrayHeaderSize + (ulong)(i * ptr)), path);
                }
            }
            else if (ti == m.StringTypeIndex)
            {
                int len = ReadInt32(s, addr + (ulong)m.ObjectHeaderSize);
                if (len < 0) continue;
                size = m.ObjectHeaderSize + 4 + ((long)len + 1) * 2;
                if ((ulong)size > s.End - addr) size = (long)(s.End - addr);
                r.StringCount++; r.StringBytes += (ulong)size;
            }
            else
            {
                size = Math.Max(m.TypeSizes[ti], m.ObjectHeaderSize);
                CrawlFields(s, addr, ti, false, tn, 0);
            }

            // Boehm/IL2CPP rounds every allocation up to the granularity — report what the heap pays.
            if (m.AllocationGranularity > 1) size = (size + m.AllocationGranularity - 1) / m.AllocationGranularity * m.AllocationGranularity;
            r.ReachableBytes += (ulong)size;
            r.ObjectCount++;
            Bump(r.ByType, tn, size);
            if (size >= 64 * 1024)
            {
                string desc = tn;
                if (m.IsArray(ti)) desc += $" [{ReadInt32(s, addr + (ulong)m.ArraySizeOffset):N0}]";
                else if (ti == m.StringTypeIndex) desc += $" (\"{PreviewString(s, addr, m)}\")";
                largest.Add((size, desc + Chain(addr)));
            }
        }

        r.Largest = largest.OrderByDescending(l => l.bytes).Take(200).ToList();
        if (r.ReachableBytes > r.HeapBytes) r.Warnings.Add("reachable bytes exceed the heap sections — a type size table mismatch; treat per-type totals as approximate");
        return r;
    }

    static bool IsPrimitiveName(string n) => n.StartsWith("System.") && n switch
    {
        "System.Boolean" or "System.Byte" or "System.SByte" or "System.Char" or "System.Int16" or "System.UInt16" or
        "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64" or "System.Single" or "System.Double" or
        "System.IntPtr" or "System.UIntPtr" or "System.Decimal" => true,
        _ => false,
    };

    static string PreviewString(HeapSection s, ulong addr, ManagedModel m)
    {
        long off = (long)(addr - s.Start) + m.ObjectHeaderSize;
        if (off + 4 > s.Bytes.LongLength) return "";
        int len = BitConverter.ToInt32(s.Bytes, (int)off);
        int take = Math.Min(len, 48);
        if (off + 4 + take * 2 > s.Bytes.LongLength) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < take; i++)
        {
            char c = (char)BitConverter.ToUInt16(s.Bytes, (int)(off + 4 + i * 2));
            sb.Append(c < 32 ? ' ' : c);
        }
        if (len > take) sb.Append('…');
        return sb.ToString();
    }

    // ─── Views ────────────────────────────────────────────────────────

    static void PrintManaged(string path, ManagedResult r, int top, string filter)
    {
        Console.WriteLine($"# {System.IO.Path.GetFileName(path)} — managed heap");
        Console.WriteLine($"  heap sections {Fmt(r.HeapBytes)} · reachable {Fmt(r.ReachableBytes)} in {r.ObjectCount:N0} objects from {r.Roots:N0} roots · unreachable/slack {Fmt(r.HeapBytes > r.ReachableBytes ? r.HeapBytes - r.ReachableBytes : 0)}");
        Console.WriteLine($"  strings {r.StringCount:N0} · {Fmt(r.StringBytes)}");
        Console.WriteLine();
        Ranked("Managed objects by type", r.ByType, top, filter);

        var big = r.Largest.Where(l => Match(l.desc, filter)).Take(top).ToList();
        if (big.Count > 0)
        {
            Console.WriteLine("## Largest managed objects (≥ 64 KB)");
            foreach (var l in big) Console.WriteLine($"  {Fmt((ulong)l.bytes),12}  {l.desc}");
            Console.WriteLine();
        }

        if (!string.IsNullOrEmpty(filter))
        {
            foreach (var t in r.ByType.Where(k => Match(k.Key, filter)).OrderByDescending(k => k.Value.bytes).Take(top))
            {
                if (!r.Referrers.TryGetValue(t.Key, out var refs)) continue;
                Console.WriteLine($"## {t.Key} — referenced from ({t.Value.count:N0} objects, {Fmt((ulong)t.Value.bytes)})");
                foreach (var p in refs.OrderByDescending(k => k.Value).Take(15))
                    Console.WriteLine($"  {p.Value,9:N0}×  {p.Key}");
                Console.WriteLine();
            }
        }
        foreach (var w in r.Warnings) Console.WriteLine($"! {w}");
    }

    static void PrintManagedDiff(string pa, ManagedResult a, string pb, ManagedResult b, int top, string filter)
    {
        Console.WriteLine($"# {System.IO.Path.GetFileName(pa)}  →  {System.IO.Path.GetFileName(pb)} — managed heap");
        DRow("Heap sections", a.HeapBytes, b.HeapBytes);
        DRow("Reachable", a.ReachableBytes, b.ReachableBytes, $"{a.ObjectCount:N0} → {b.ObjectCount:N0} objects");
        DRow("Strings", a.StringBytes, b.StringBytes, $"{a.StringCount:N0} → {b.StringCount:N0}");
        Console.WriteLine();
        RankedDiff("Managed objects by type — biggest changes", a.ByType, b.ByType, top, filter);
    }
}
