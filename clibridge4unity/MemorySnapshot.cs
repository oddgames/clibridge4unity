using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace clibridge4unity;

/// <summary>
/// Reads Unity Memory Profiler .snap files without the Editor, and compares them.
///
/// The format is a chaptered binary container, documented in the Memory Profiler package's own
/// reader (FileReader.cs). This is a direct port of that layout, not a guess:
///
///   file[0..4]          header signature 0xAEABCDCD
///   file[len-12..len-4] directory address          file[len-4..len] footer signature 0xABCDCDAE
///   directory:  sig 0xCDCDAEAB · version 0x20170724 · block-section address · int chapterCount ·
///               long[chapterCount] chapterAddress (0 = chapter absent)
///   block section: version · int blockCount · long[blockCount] blockAddress
///   block:      ulong chunkSize · ulong totalBytes · long[ceil(total/chunk)] chunkFileOffset
///   chapter:    18-byte header (ushort format, uint blockIndex, uint entriesMeta, ulong headerMeta)
///               then, for dynamic-size chapters, long[count] element offsets
///
/// Every chapter owns one block whose chunks are NOT contiguous in the file — element reads walk
/// the chunk table. Chapter and block counts are read from the file rather than assumed, which is
/// what lets one reader open captures from several Unity versions: a chapter that does not exist
/// in an older capture simply has address 0.
///
/// The managed heap is crawled too (MemorySnapshotManaged.cs): the package's own root-and-walk
/// algorithm over the type/field tables, giving "N instances of Foo, held by what" for player
/// captures. Editor captures are refused there (multi-GB heaps); the native side — objects, types,
/// allocators, labels, graphics resources, the memory stats summary — is available for any capture.
/// </summary>
static partial class MemorySnapshot
{
    // ─── Chapter ids — lifted verbatim from the package's EntryType.cs ───

    enum EntryType : ushort
    {
        Metadata_Version = 0,
        Metadata_RecordDate = 1,
        Metadata_UserMetadata = 2,
        Metadata_CaptureFlags = 3,
        Metadata_VirtualMachineInformation = 4,
        NativeTypes_Name = 5,
        NativeTypes_NativeBaseTypeArrayIndex = 6,
        NativeObjects_NativeTypeArrayIndex = 7,
        NativeObjects_HideFlags = 8,
        NativeObjects_Flags = 9,
        NativeObjects_InstanceId = 10,
        NativeObjects_Name = 11,
        NativeObjects_NativeObjectAddress = 12,
        NativeObjects_Size = 13,
        NativeObjects_RootReferenceId = 14,
        GCHandles_Target = 15,
        Connections_From = 16,
        Connections_To = 17,
        ManagedHeapSections_StartAddress = 18,
        ManagedHeapSections_Bytes = 19,
        ManagedStacks_StartAddress = 20,
        ManagedStacks_Bytes = 21,
        TypeDescriptions_Flags = 22,
        TypeDescriptions_Name = 23,
        TypeDescriptions_Assembly = 24,
        TypeDescriptions_FieldIndices = 25,
        TypeDescriptions_StaticFieldBytes = 26,
        TypeDescriptions_BaseOrElementTypeIndex = 27,
        TypeDescriptions_Size = 28,
        TypeDescriptions_TypeInfoAddress = 29,
        TypeDescriptions_TypeIndex = 30,
        FieldDescriptions_Offset = 31,
        FieldDescriptions_TypeIndex = 32,
        FieldDescriptions_Name = 33,
        FieldDescriptions_IsStatic = 34,
        NativeRootReferences_Id = 35,
        NativeRootReferences_AreaName = 36,
        NativeRootReferences_ObjectName = 37,
        NativeRootReferences_AccumulatedSize = 38,
        NativeAllocations_MemoryRegionIndex = 39,
        NativeAllocations_RootReferenceId = 40,
        NativeAllocations_AllocationSiteId = 41,
        NativeAllocations_Address = 42,
        NativeAllocations_Size = 43,
        NativeAllocations_OverheadSize = 44,
        NativeAllocations_PaddingSize = 45,
        NativeMemoryRegions_Name = 46,
        NativeMemoryRegions_ParentIndex = 47,
        NativeMemoryRegions_AddressBase = 48,
        NativeMemoryRegions_AddressSize = 49,
        NativeMemoryRegions_FirstAllocationIndex = 50,
        NativeMemoryRegions_NumAllocations = 51,
        NativeMemoryLabels_Name = 52,
        NativeAllocationSites_Id = 53,
        NativeAllocationSites_MemoryLabelIndex = 54,
        NativeAllocationSites_CallstackSymbols = 55,
        NativeCallstackSymbol_Symbol = 56,
        NativeCallstackSymbol_ReadableStackTrace = 57,
        NativeObjects_GCHandleIndex = 58,
        ProfileTarget_Info = 59,
        ProfileTarget_MemoryStats = 60,
        NativeMemoryLabels_Size = 61,
        SceneObjects_Name = 62,
        SceneObjects_Path = 63,
        SceneObjects_AssetPath = 64,
        SceneObjects_BuildIndex = 65,
        SceneObjects_RootIdCounts = 66,
        SceneObjects_RootIdOffsets = 67,
        SceneObjects_RootIds = 68,
        NativeMemoryLabels_AllocatorIdentifier = 69,
        NativeGfxResourceReferences_Id = 70,
        NativeGfxResourceReferences_Size = 71,
        NativeGfxResourceReferences_RootId = 72,
        NativeAllocatorInfo_AllocatorName = 73,
        NativeAllocatorInfo_Identifier = 74,
        NativeAllocatorInfo_UsedSize = 75,
        NativeAllocatorInfo_ReservedSize = 76,
        NativeAllocatorInfo_OverheadSize = 77,
        NativeAllocatorInfo_PeakUsedSize = 78,
        NativeAllocatorInfo_AllocationCount = 79,
        NativeAllocatorInfo_Flags = 80,
        ObjectMetaData_MetaDataBufferIndicies = 81,
        ObjectMetaData_MetaDataBuffer = 82,
        SystemMemoryRegions_Address = 83,
        SystemMemoryRegions_Size = 84,
        SystemMemoryRegions_Resident = 85,
        SystemMemoryRegions_Protection = 86,
        SystemMemoryRegions_Name = 87,
        SystemMemoryResidentPages_Address = 88,
        SystemMemoryResidentPages_FirstPageIndex = 89,
        SystemMemoryResidentPages_LastPageIndex = 90,
        SystemMemoryResidentPages_PagesState = 91,
        SystemMemoryResidentPages_PageSize = 92,
        Count = 93,
    }

    // ─── Container reader ─────────────────────────────────────────────

    sealed class SnapFile : IDisposable
    {
        const uint HeaderSig = 0xAEABCDCD, DirSig = 0xCDCDAEAB, FooterSig = 0xABCDCDAE, SectionVersion = 0x20170724;

        struct Entry { public ushort Format; public uint BlockIndex; public uint EntriesMeta; public ulong HeaderMeta; public long[] Offsets; }
        struct Block { public ulong ChunkSize; public ulong TotalBytes; public long[] ChunkOffsets; }

        readonly FileStream _fs;
        readonly BinaryReader _br;
        Entry[] _entries = Array.Empty<Entry>();
        Block[] _blocks = Array.Empty<Block>();

        public string Path { get; }
        public uint FormatVersion { get; private set; }

        public SnapFile(string path)
        {
            Path = path;
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            _br = new BinaryReader(_fs);
            Open();
        }

        public void Dispose() { _br.Dispose(); _fs.Dispose(); }

        void Open()
        {
            long len = _fs.Length;
            if (len < 32) throw new InvalidDataException("file too small to be a snapshot");

            _fs.Position = 0;
            if (_br.ReadUInt32() != HeaderSig) throw new InvalidDataException("bad header signature — not a Unity memory snapshot");
            _fs.Position = len - 4;
            if (_br.ReadUInt32() != FooterSig) throw new InvalidDataException("bad footer signature — truncated or not a snapshot");
            _fs.Position = len - 12;
            long dir = _br.ReadInt64();
            if (dir <= 0 || dir >= len) throw new InvalidDataException("directory address out of range");

            _fs.Position = dir;
            if (_br.ReadUInt32() != DirSig) throw new InvalidDataException("bad directory signature");
            if (_br.ReadUInt32() != SectionVersion) throw new InvalidDataException("unsupported chapter section version");
            long blockSection = _br.ReadInt64();
            int chapterCount = _br.ReadInt32();
            var chapterAddr = new long[chapterCount];
            for (int i = 0; i < chapterCount; i++) chapterAddr[i] = _br.ReadInt64();

            _fs.Position = blockSection;
            if (_br.ReadUInt32() != SectionVersion) throw new InvalidDataException("unsupported block section version");
            int blockCount = _br.ReadInt32();
            if (blockCount < 1) throw new InvalidDataException("no data blocks");
            var blockAddr = new long[blockCount];
            for (int i = 0; i < blockCount; i++) blockAddr[i] = _br.ReadInt64();

            _blocks = new Block[blockCount];
            for (int i = 0; i < blockCount; i++)
            {
                _fs.Position = blockAddr[i];
                var b = new Block { ChunkSize = _br.ReadUInt64(), TotalBytes = _br.ReadUInt64() };
                ulong n = b.ChunkSize == 0 ? 0 : (b.TotalBytes / b.ChunkSize) + (b.TotalBytes % b.ChunkSize != 0 ? 1UL : 0UL);
                b.ChunkOffsets = new long[n];
                for (ulong k = 0; k < n; k++) b.ChunkOffsets[k] = _br.ReadInt64();
                _blocks[i] = b;
            }

            // Chapter headers are packed back-to-back and are NOT all 18 bytes, whatever the
            // package's EntryHeader struct suggests. Measured against real files:
            //   single  : fmt u16 · block u32 · byteSize u32 · blockOffset u64        (18 bytes)
            //   const   : fmt u16 · block u32 · elemSize u32 · count u32              (14 bytes)
            //   dynamic : fmt u16 · block u32 · count u32 · (count+1) x i64 offsets   (10 + 8(n+1))
            // The package reads a fixed 18 and overruns into the next header, then hides it by
            // casting the count to uint. Reading the true widths avoids inheriting that.
            _entries = new Entry[Math.Max(chapterCount, (int)EntryType.Count)];
            for (int i = 0; i < chapterCount; i++)
            {
                if (chapterAddr[i] == 0) continue;
                _fs.Position = chapterAddr[i];
                var e = new Entry
                {
                    Format = _br.ReadUInt16(),
                    BlockIndex = _br.ReadUInt32(),
                    EntriesMeta = _br.ReadUInt32(),
                };
                switch (e.Format)
                {
                    case 1: e.HeaderMeta = _br.ReadUInt64(); break;           // block offset
                    case 2: e.HeaderMeta = _br.ReadUInt32(); break;           // element count
                    case 3:
                        e.Offsets = new long[e.EntriesMeta + 1];              // offsets[count] = total
                        for (uint k = 0; k <= e.EntriesMeta; k++) e.Offsets[k] = _br.ReadInt64();
                        e.HeaderMeta = (ulong)e.Offsets[e.EntriesMeta];
                        break;
                }
                _entries[i] = e;
            }

            FormatVersion = Has(EntryType.Metadata_Version) ? BitConverter.ToUInt32(ReadAll(EntryType.Metadata_Version), 0) : 0;
        }

        public bool Has(EntryType t) => (int)t < _entries.Length && _entries[(int)t].Format != 0;

        public string FormatName(EntryType t) => _entries[(int)t].Format switch { 1 => "single", 2 => "const", 3 => "dynamic", _ => "?" };

        public uint Count(EntryType t)
        {
            if (!Has(t)) return 0;
            var e = _entries[(int)t];
            return e.Format switch { 1 => 1u, 2 => (uint)e.HeaderMeta, 3 => e.EntriesMeta, _ => 0u };
        }

        /// <summary>Total data bytes in a chapter, without reading them — sums a heap dump cheaply.</summary>
        public ulong TotalBytes(EntryType t)
        {
            if (!Has(t)) return 0;
            var e = _entries[(int)t];
            return e.Format switch { 1 => e.EntriesMeta, 2 => e.EntriesMeta * e.HeaderMeta, 3 => e.HeaderMeta, _ => 0UL };
        }

        /// <summary>
        /// Whole chapter. Blocks are SHARED between chapters: a single-element chapter sits at
        /// HeaderMeta bytes into its block (several metadata blobs pack into one block), whereas
        /// const-size and dynamic-size arrays each start at 0 of their own. Getting this wrong is
        /// silent — chapter 0 reads fine because it happens to be first, and everything after it
        /// returns a neighbour's bytes.
        /// </summary>
        public byte[] ReadAll(EntryType t)
        {
            if (!Has(t)) return Array.Empty<byte>();
            var e = _entries[(int)t];
            long start = e.Format == 1 ? (long)e.HeaderMeta : 0;
            return ReadBlock(e.BlockIndex, start, (long)TotalBytes(t));
        }

        public byte[] ReadElement(EntryType t, long index)
        {
            var e = _entries[(int)t];
            switch (e.Format)
            {
                case 1: return ReadBlock(e.BlockIndex, (long)e.HeaderMeta, e.EntriesMeta);
                case 2: return ReadBlock(e.BlockIndex, index * e.EntriesMeta, e.EntriesMeta);
                case 3:
                    return ReadBlock(e.BlockIndex, e.Offsets[index], e.Offsets[index + 1] - e.Offsets[index]);
                default: return Array.Empty<byte>();
            }
        }

        public string[] ReadStrings(EntryType t)
        {
            if (!Has(t)) return Array.Empty<string>();
            var e = _entries[(int)t];
            if (e.Format != 3) return Array.Empty<string>();
            // One read of the whole block, then slice — per-element seeks on 100k names is slow.
            byte[] all = ReadAll(t);
            var result = new string[e.EntriesMeta];
            for (int i = 0; i < result.Length; i++)
            {
                long s = e.Offsets[i];
                long en = e.Offsets[i + 1];
                int n = (int)(en - s);
                while (n > 0 && all[s + n - 1] == 0) n--;   // strings are NUL-padded
                result[i] = Encoding.UTF8.GetString(all, (int)s, n);
            }
            return result;
        }

        public ulong[] ReadULongs(EntryType t) => Fixed(t, 8, (b, i) => BitConverter.ToUInt64(b, i));
        public long[] ReadLongs(EntryType t) => Fixed(t, 8, (b, i) => BitConverter.ToInt64(b, i));
        public int[] ReadInts(EntryType t) => Fixed(t, 4, (b, i) => BitConverter.ToInt32(b, i));
        public uint[] ReadUInts(EntryType t) => Fixed(t, 4, (b, i) => BitConverter.ToUInt32(b, i));

        T[] Fixed<T>(EntryType t, int size, Func<byte[], int, T> conv)
        {
            if (!Has(t)) return Array.Empty<T>();
            var e = _entries[(int)t];
            if (e.Format != 2 || e.EntriesMeta != size) return Array.Empty<T>();
            byte[] all = ReadAll(t);
            var r = new T[all.Length / size];
            for (int i = 0; i < r.Length; i++) r[i] = conv(all, i * size);
            return r;
        }

        /// <summary>Read a logical byte range out of a block, walking its non-contiguous chunks.</summary>
        byte[] ReadBlock(uint blockIndex, long pos, long length)
        {
            if (length <= 0 || blockIndex >= _blocks.Length) return Array.Empty<byte>();
            var b = _blocks[blockIndex];
            var result = new byte[length];
            long done = 0;
            while (done < length)
            {
                long p = pos + done;
                long chunk = (long)((ulong)p / b.ChunkSize);
                long inChunk = (long)((ulong)p % b.ChunkSize);
                long take = Math.Min(length - done, (long)b.ChunkSize - inChunk);
                _fs.Position = b.ChunkOffsets[chunk] + inChunk;
                int got = _fs.Read(result, (int)done, (int)take);
                if (got <= 0) throw new EndOfStreamException("snapshot truncated inside block " + blockIndex);
                done += got;
            }
            return result;
        }
    }

    // ─── What we extract ──────────────────────────────────────────────

    sealed class Summary
    {
        public string Path, Product = "?", UnityVersion = "?", Platform = "?", Backend = "?";
        public DateTime RecordDate;
        public uint FormatVersion;
        public ulong TotalPhysical, TotalGraphicsDevice;
        public ulong TotalUsed, TotalReserved, Gfx, Audio, GcUsed, GcReserved, ProfilerUsed;
        public ulong ManagedHeapBytes;
        public int NativeObjectCount;
        public ulong NativeObjectBytes;
        public Dictionary<string, (long bytes, int count)> ByType = new();
        public Dictionary<string, (long bytes, int count)> ByObject = new();   // "Type:name"
        public Dictionary<string, long> Labels = new();
        public Dictionary<string, (long used, long reserved, long peak)> Allocators = new();
        public ulong GfxResourceBytes;
        public List<string> Warnings = new();
    }

    static Summary Summarize(SnapFile f)
    {
        var s = new Summary { Path = f.Path, FormatVersion = f.FormatVersion };

        if (f.Has(EntryType.Metadata_RecordDate))
        {
            long ticks = BitConverter.ToInt64(f.ReadAll(EntryType.Metadata_RecordDate), 0);
            try { s.RecordDate = new DateTime(ticks); } catch { }
        }

        // ProfileTarget_Info — Sequential, Pack 4, Size 512 (offsets from the package struct).
        if (f.Has(EntryType.ProfileTarget_Info))
        {
            var b = f.ReadAll(EntryType.ProfileTarget_Info);
            if (b.Length >= 328)
            {
                // Sequential with natural alignment: ulongs land on 8-byte boundaries, so the
                // fields after the three leading ints start at 16, not 12.
                s.Platform = RuntimePlatformName(BitConverter.ToInt32(b, 4));
                s.TotalPhysical = BitConverter.ToUInt64(b, 16);
                s.TotalGraphicsDevice = BitConverter.ToUInt64(b, 24);
                s.Backend = BitConverter.ToInt32(b, 32) switch { 0 => "Mono", 1 => "IL2CPP", _ => "?" };
                uint vl = BitConverter.ToUInt32(b, 48);
                s.UnityVersion = Encoding.UTF8.GetString(b, 52, (int)Math.Min(vl, 16)).TrimEnd(' ');
                uint pl = BitConverter.ToUInt32(b, 68);
                s.Product = Encoding.UTF8.GetString(b, 72, (int)Math.Min(pl, 256)).TrimEnd(' ');
            }
        }

        if (f.Has(EntryType.ProfileTarget_MemoryStats))
        {
            var b = f.ReadAll(EntryType.ProfileTarget_MemoryStats);
            if (b.Length >= 96)
            {
                s.TotalUsed = BitConverter.ToUInt64(b, 8);
                s.TotalReserved = BitConverter.ToUInt64(b, 16);
                s.Gfx = BitConverter.ToUInt64(b, 32);
                s.Audio = BitConverter.ToUInt64(b, 40);
                s.GcUsed = BitConverter.ToUInt64(b, 48);
                s.GcReserved = BitConverter.ToUInt64(b, 56);
                s.ProfilerUsed = BitConverter.ToUInt64(b, 64);
            }
        }

        s.ManagedHeapBytes = f.TotalBytes(EntryType.ManagedHeapSections_Bytes);

        // Native objects → by type and by (type, name).
        var typeNames = f.ReadStrings(EntryType.NativeTypes_Name);
        var objNames = f.ReadStrings(EntryType.NativeObjects_Name);
        var objType = f.ReadInts(EntryType.NativeObjects_NativeTypeArrayIndex);
        var objSize = f.ReadULongs(EntryType.NativeObjects_Size);
        int n = Math.Min(objNames.Length, Math.Min(objType.Length, objSize.Length));
        if (objNames.Length != objType.Length || objType.Length != objSize.Length)
            s.Warnings.Add($"native object columns disagree: names={objNames.Length} types={objType.Length} sizes={objSize.Length}");
        s.NativeObjectCount = n;
        for (int i = 0; i < n; i++)
        {
            string type = objType[i] >= 0 && objType[i] < typeNames.Length ? typeNames[objType[i]] : "?";
            long size = (long)objSize[i];
            s.NativeObjectBytes += objSize[i];
            Bump(s.ByType, type, size);
            Bump(s.ByObject, type + ":" + StableName(objNames[i]), size);
        }

        // Memory labels (v12+ carries sizes).
        var labelNames = f.ReadStrings(EntryType.NativeMemoryLabels_Name);
        var labelSizes = f.ReadULongs(EntryType.NativeMemoryLabels_Size);
        for (int i = 0; i < Math.Min(labelNames.Length, labelSizes.Length); i++)
            s.Labels[labelNames[i]] = s.Labels.GetValueOrDefault(labelNames[i]) + (long)labelSizes[i];

        // Allocators (v14+).
        var allocNames = f.ReadStrings(EntryType.NativeAllocatorInfo_AllocatorName);
        var allocUsed = f.ReadULongs(EntryType.NativeAllocatorInfo_UsedSize);
        var allocRes = f.ReadULongs(EntryType.NativeAllocatorInfo_ReservedSize);
        var allocPeak = f.ReadULongs(EntryType.NativeAllocatorInfo_PeakUsedSize);
        for (int i = 0; i < allocNames.Length; i++)
            s.Allocators[allocNames[i]] = (
                i < allocUsed.Length ? (long)allocUsed[i] : 0,
                i < allocRes.Length ? (long)allocRes[i] : 0,
                i < allocPeak.Length ? (long)allocPeak[i] : 0);

        foreach (var g in f.ReadULongs(EntryType.NativeGfxResourceReferences_Size)) s.GfxResourceBytes += g;
        return s;
    }

    /// <summary>
    /// Unity's pooled render textures are named "TempBuffer N WxH" with N assigned per session,
    /// so an identical 31 MB buffer shows up as GONE + NEW across two captures. Dropping the index
    /// makes the diff report it as unchanged — which is the truth — while keeping the dimensions,
    /// which are what actually distinguish one temp buffer from another.
    /// </summary>
    static string StableName(string name)
    {
        if (name.StartsWith("TempBuffer ", StringComparison.Ordinal))
        {
            int sp = name.IndexOf(' ', 11);
            if (sp > 0) return "TempBuffer " + name.Substring(sp + 1);
        }
        return name;
    }

    static void Bump(Dictionary<string, (long bytes, int count)> d, string key, long bytes)
    {
        d.TryGetValue(key, out var cur);
        d[key] = (cur.bytes + bytes, cur.count + 1);
    }

    static string RuntimePlatformName(int p) => p switch
    {
        0 => "OSXEditor", 1 => "OSXPlayer", 2 => "WindowsPlayer", 7 => "WindowsEditor", 8 => "iPhonePlayer",
        11 => "Android", 13 => "LinuxPlayer", 16 => "LinuxEditor", 17 => "WebGLPlayer", 31 => "PS4",
        33 => "XboxOne", 37 => "tvOS", 38 => "Switch", 44 => "PS5", _ => "platform " + p,
    };

    // ─── Command ──────────────────────────────────────────────────────

    public static int Run(string args)
    {
        var parts = SplitArgs(args);
        var files = new List<string>();
        string view = "summary";
        int top = 25;
        string filter = null;

        // Snapshot names routinely contain spaces ("Ipad Air 3rd Generation - Main Menu.snap")
        // and the shell has already eaten any quotes by the time the CLI sees them. So a path
        // is recognised greedily: keep joining tokens until the result exists on disk.
        for (int i = 0; i < parts.Count; i++)
        {
            string p = parts[i];
            string l = p.ToLowerInvariant();
            if (l.StartsWith("top:") && int.TryParse(l.Substring(4), out int t)) { top = t; continue; }
            if (l.StartsWith("filter:")) { filter = p.Substring(7); continue; }
            if (l is "types" or "objects" or "labels" or "allocators" or "summary" or "chapters" or "managed") { view = l; continue; }

            string found = null; int consumed = 0;
            var sb = new StringBuilder();
            for (int j = i; j < parts.Count; j++)
            {
                if (j > i) sb.Append(' ');
                sb.Append(parts[j]);
                string cand = sb.ToString();
                if (File.Exists(cand) || Directory.Exists(cand)) { found = cand; consumed = j - i; }
            }
            if (found == null) { Console.Error.WriteLine($"Not found: {p}"); return 1; }
            if (Directory.Exists(found)) files.AddRange(Directory.GetFiles(found, "*.snap").OrderBy(x => x));
            else files.Add(found);
            i += consumed;
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("Usage: MEMSNAP <a.snap> [b.snap] [summary|types|objects|labels|allocators|managed] [top:N] [filter:X]");
            Console.Error.WriteLine("       MEMSNAP <folder>            (summary of every .snap, in one table)");
            return 1;
        }

        try
        {
            if (view == "chapters")
            {
                foreach (var path in files)
                {
                    using var f = new SnapFile(path);
                    Console.WriteLine($"# {System.IO.Path.GetFileName(path)}   format v{f.FormatVersion}");
                    Console.WriteLine($"  {"chapter",-44} {"fmt",-8} {"count",12} {"bytes",14}");
                    for (int i = 0; i < (int)EntryType.Count; i++)
                    {
                        var t = (EntryType)i;
                        if (!f.Has(t)) continue;
                        Console.WriteLine($"  {t,-44} {f.FormatName(t),-8} {f.Count(t),12:N0} {Fmt(f.TotalBytes(t)),14}");
                    }
                    Console.WriteLine();
                }
                return 0;
            }

            if (view == "managed")
            {
                if (files.Count > 2) { Console.Error.WriteLine("managed: give one capture, or two to diff"); return 1; }
                var crawls = new List<ManagedResult>();
                foreach (var path in files)
                {
                    using var f = new SnapFile(path);
                    crawls.Add(CrawlManaged(f));
                }
                if (crawls.Count == 1) PrintManaged(files[0], crawls[0], top, filter);
                else PrintManagedDiff(files[0], crawls[0], files[1], crawls[1], top, filter);
                return 0;
            }

            var sums = new List<Summary>();
            foreach (var path in files)
            {
                using var f = new SnapFile(path);
                sums.Add(Summarize(f));
            }

            if (sums.Count == 1) { PrintOne(sums[0], view, top, filter); return 0; }
            if (sums.Count == 2 && view != "summary") { PrintDiff(sums[0], sums[1], view, top, filter); return 0; }
            if (sums.Count == 2) { PrintDiff(sums[0], sums[1], "summary", top, filter); return 0; }
            PrintTable(sums);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static void PrintOne(Summary s, string view, int top, string filter)
    {
        Console.WriteLine($"# {System.IO.Path.GetFileName(s.Path)}");
        Console.WriteLine($"  {s.Product}  ·  Unity {s.UnityVersion}  ·  {s.Platform} / {s.Backend}  ·  captured {s.RecordDate:yyyy-MM-dd HH:mm:ss}  ·  format v{s.FormatVersion}");
        Console.WriteLine();
        if (view is "summary" or "types" or "objects" or "labels" or "allocators")
        {
            Console.WriteLine("## Memory");
            Row("Total used",     s.TotalUsed);      Row("Total reserved", s.TotalReserved);
            Row("Graphics",       s.Gfx);            Row("Audio",          s.Audio);
            Row("GC heap used",   s.GcUsed);         Row("GC heap reserved", s.GcReserved);
            Row("Managed heap sections", s.ManagedHeapBytes);
            Row("Native objects", s.NativeObjectBytes, $"{s.NativeObjectCount:N0} objects");
            Row("Gfx resources",  s.GfxResourceBytes);
            Row("Device RAM",     s.TotalPhysical);
            Console.WriteLine();
        }
        if (view is "summary" or "types")
            Ranked("Native objects by type", s.ByType, top, filter);
        if (view is "summary" or "objects")
            Ranked("Largest native objects", s.ByObject, view == "summary" ? Math.Min(top, 15) : top, filter);
        if (view is "summary" or "labels")
            RankedFlat("Memory labels", s.Labels, top, filter);
        if (view is "summary" or "allocators")
            Allocs(s, top, filter);
        foreach (var w in s.Warnings) Console.WriteLine($"! {w}");
    }

    static void PrintDiff(Summary a, Summary b, string view, int top, string filter)
    {
        Console.WriteLine($"# {System.IO.Path.GetFileName(a.Path)}  →  {System.IO.Path.GetFileName(b.Path)}");
        Console.WriteLine($"  {a.Product} · Unity {a.UnityVersion} · {a.Platform}   |   {a.RecordDate:HH:mm:ss} → {b.RecordDate:HH:mm:ss}");
        Console.WriteLine();
        Console.WriteLine("## Memory (delta)");
        DRow("Total used",     a.TotalUsed, b.TotalUsed);
        DRow("Total reserved", a.TotalReserved, b.TotalReserved);
        DRow("Graphics",       a.Gfx, b.Gfx);
        DRow("Audio",          a.Audio, b.Audio);
        DRow("GC heap used",   a.GcUsed, b.GcUsed);
        DRow("Managed heap sections", a.ManagedHeapBytes, b.ManagedHeapBytes);
        DRow("Native objects", a.NativeObjectBytes, b.NativeObjectBytes, $"{a.NativeObjectCount:N0} → {b.NativeObjectCount:N0} objects");
        DRow("Gfx resources",  a.GfxResourceBytes, b.GfxResourceBytes);
        Console.WriteLine();

        if (view is "summary" or "types")
            RankedDiff("Native objects by type — biggest changes", a.ByType, b.ByType, top, filter);
        if (view is "summary" or "objects")
            RankedDiff("Native objects — new, gone, grown", a.ByObject, b.ByObject, view == "summary" ? Math.Min(top, 20) : top, filter);
        if (view is "summary" or "labels")
            RankedDiffFlat("Memory labels — biggest changes", a.Labels, b.Labels, top, filter);
        if (view is "summary" or "allocators")
            RankedDiffFlat("Allocators (used) — biggest changes",
                a.Allocators.ToDictionary(k => k.Key, k => k.Value.used),
                b.Allocators.ToDictionary(k => k.Key, k => k.Value.used), top, filter);
    }

    static void PrintTable(List<Summary> sums)
    {
        Console.WriteLine("# Snapshot overview");
        Console.WriteLine();
        Console.WriteLine($"{"capture",-48} {"total used",12} {"gfx",10} {"gc heap",10} {"native objs",12} {"objects",9}");
        foreach (var s in sums)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(s.Path);
            if (name.Length > 46) name = name.Substring(0, 45) + "…";
            Console.WriteLine($"{name,-48} {Fmt(s.TotalUsed),12} {Fmt(s.Gfx),10} {Fmt(s.GcUsed),10} {Fmt(s.NativeObjectBytes),12} {s.NativeObjectCount,9:N0}");
        }
        Console.WriteLine();
        Console.WriteLine("MEMSNAP <a> <b> compares two of them.");
    }

    // ─── Rendering helpers ────────────────────────────────────────────

    static void Row(string label, ulong bytes, string extra = null)
        => Console.WriteLine($"  {label,-24} {Fmt(bytes),12}{(extra == null ? "" : "   " + extra)}");

    static void DRow(string label, ulong a, ulong b, string extra = null)
    {
        long d = (long)b - (long)a;
        Console.WriteLine($"  {label,-24} {Fmt(a),12} → {Fmt(b),12}   {Signed(d),12}{(extra == null ? "" : "   " + extra)}");
    }

    static void Ranked(string title, Dictionary<string, (long bytes, int count)> d, int top, string filter)
    {
        Console.WriteLine($"## {title}");
        var rows = d.Where(k => Match(k.Key, filter)).OrderByDescending(k => k.Value.bytes).Take(top).ToList();
        foreach (var r in rows) Console.WriteLine($"  {Fmt((ulong)r.Value.bytes),12}  {r.Value.count,7:N0}×  {r.Key}");
        if (rows.Count == 0) Console.WriteLine("  (nothing)");
        Console.WriteLine();
    }

    static void RankedFlat(string title, Dictionary<string, long> d, int top, string filter)
    {
        if (d.Count == 0) return;
        Console.WriteLine($"## {title}");
        foreach (var r in d.Where(k => Match(k.Key, filter)).OrderByDescending(k => k.Value).Take(top))
            Console.WriteLine($"  {Fmt((ulong)Math.Max(0, r.Value)),12}  {r.Key}");
        Console.WriteLine();
    }

    static void Allocs(Summary s, int top, string filter)
    {
        if (s.Allocators.Count == 0) return;
        Console.WriteLine("## Allocators");
        Console.WriteLine($"  {"used",12} {"reserved",12} {"peak",12}  name");
        foreach (var a in s.Allocators.Where(k => Match(k.Key, filter)).OrderByDescending(k => k.Value.used).Take(top))
            Console.WriteLine($"  {Fmt((ulong)a.Value.used),12} {Fmt((ulong)a.Value.reserved),12} {Fmt((ulong)a.Value.peak),12}  {a.Key}");
        Console.WriteLine();
    }

    static void RankedDiff(string title, Dictionary<string, (long bytes, int count)> a, Dictionary<string, (long bytes, int count)> b, int top, string filter)
    {
        Console.WriteLine($"## {title}");
        var keys = new HashSet<string>(a.Keys); keys.UnionWith(b.Keys);
        var rows = keys.Where(k => Match(k, filter)).Select(k =>
        {
            a.TryGetValue(k, out var va); b.TryGetValue(k, out var vb);
            return (key: k, da: va, db: vb, delta: vb.bytes - va.bytes);
        }).Where(r => r.delta != 0).OrderByDescending(r => Math.Abs(r.delta)).Take(top).ToList();
        foreach (var r in rows)
        {
            string tag = r.da.count == 0 ? "NEW " : r.db.count == 0 ? "GONE" : "    ";
            Console.WriteLine($"  {Signed(r.delta),12}  {tag} {Fmt((ulong)r.da.bytes),10} → {Fmt((ulong)r.db.bytes),10}  {r.da.count,6:N0}→{r.db.count,-6:N0} {r.key}");
        }
        if (rows.Count == 0) Console.WriteLine("  (no change)");
        Console.WriteLine();
    }

    static void RankedDiffFlat(string title, Dictionary<string, long> a, Dictionary<string, long> b, int top, string filter)
    {
        if (a.Count == 0 && b.Count == 0) return;
        Console.WriteLine($"## {title}");
        var keys = new HashSet<string>(a.Keys); keys.UnionWith(b.Keys);
        var rows = keys.Where(k => Match(k, filter))
            .Select(k => (key: k, va: a.GetValueOrDefault(k), vb: b.GetValueOrDefault(k)))
            .Select(r => (r.key, r.va, r.vb, delta: r.vb - r.va))
            .Where(r => r.delta != 0).OrderByDescending(r => Math.Abs(r.delta)).Take(top).ToList();
        foreach (var r in rows)
            Console.WriteLine($"  {Signed(r.delta),12}  {Fmt((ulong)Math.Max(0, r.va)),10} → {Fmt((ulong)Math.Max(0, r.vb)),10}  {r.key}");
        if (rows.Count == 0) Console.WriteLine("  (no change)");
        Console.WriteLine();
    }

    static bool Match(string key, string filter)
        => string.IsNullOrEmpty(filter) || key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    static string Fmt(ulong bytes)
    {
        double b = bytes;
        if (b >= 1L << 30) return $"{b / (1L << 30):0.00} GB";
        if (b >= 1L << 20) return $"{b / (1L << 20):0.0} MB";
        if (b >= 1L << 10) return $"{b / (1L << 10):0.0} KB";
        return $"{bytes} B";
    }

    static string Signed(long delta) => (delta >= 0 ? "+" : "-") + Fmt((ulong)Math.Abs(delta));

    /// <summary>Split on spaces but keep quoted paths together — snapshot names have spaces in them.</summary>
    static List<string> SplitArgs(string args)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(args)) return result;
        var cur = new StringBuilder(); bool q = false;
        foreach (char c in args)
        {
            if (c == '"') { q = !q; continue; }
            if (c == ' ' && !q) { if (cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); } continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        return result;
    }
}
