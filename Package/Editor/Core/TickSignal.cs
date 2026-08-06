using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace clibridge4unity
{
    /// <summary>
    /// Cross-process "a client is waiting" flag, shared by the CLI and the Unity package via a
    /// 16-byte memory-mapped file. Linked into the CLI build — must stay free of UnityEditor types.
    ///
    /// WHY this exists at all: for an ordinary command the server already knows work is pending, so
    /// no IPC is needed. The gap is the domain-reload window — the BridgeServer instance is gone, the
    /// pipe is down, and the CLI is sitting in its reconnect loop with no way to tell Unity anyone is
    /// there. Unity throttles its editor loop while unfocused, so that wait can stretch for minutes.
    /// A flag on disk survives the reload (the file outlives the AppDomain) and costs ~61 ns to read,
    /// against ~21 us for File.Exists and ~37 us for a directory sweep — cheap enough for the 10 ms
    /// wake loop to poll every cycle.
    ///
    /// WHY a deadline and not a boolean: a CLI that is killed mid-wait — which happens, users cancel
    /// hung commands — would leave a boolean set forever and peg an editor core indefinitely, a worse
    /// failure than the one this fixes. Callers must keep re-arming the deadline while they wait, so
    /// the signal self-heals within <see cref="DefaultLease"/> of the client disappearing.
    /// </summary>
    internal sealed class TickSignal : IDisposable
    {
        private const int MapSize = 16;
        private const int LayoutVersion = 1;
        // Offsets: 0..7 expiry (int64 UTC ticks), 8..11 waiter pid, 12..15 layout version.
        private const int OffExpiry = 0;
        private const int OffPid = 8;
        private const int OffVersion = 12;

        /// <summary>How long a single Request keeps the editor ticking. Long enough to cover a poll
        /// gap, short enough that a dead client stops mattering almost immediately.</summary>
        public static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(5);

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;

        public static string PathFor(string projectPath)
            => Path.Combine(projectPath, ".clibridge4unity", "tick.flag");

        /// <summary>Map the signal. Returns null when unavailable — every caller treats that as
        /// "no signalling", falling back to existing behaviour rather than failing.</summary>
        public static TickSignal Open(string projectPath, bool create)
        {
            try
            {
                string path = PathFor(projectPath);
                if (!File.Exists(path))
                {
                    if (!create) return null;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    // Pre-size the file; a mapping cannot grow it.
                    using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
                        fs.SetLength(MapSize);
                }

                var signal = new TickSignal();
                // CreateFromFile, not a named map: .NET on Unix has no named maps without a backing
                // file, and this has to work wherever the CLI is shipped.
                signal._mmf = MemoryMappedFile.CreateFromFile(
                    new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite),
                    null, MapSize, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None,
                    leaveOpen: false);
                signal._view = signal._mmf.CreateViewAccessor(0, MapSize, MemoryMappedFileAccess.ReadWrite);
                return signal;
            }
            catch { return null; }
        }

        /// <summary>Ask the editor to keep ticking for <paramref name="lease"/> from now. Safe to call
        /// repeatedly; callers waiting on a long operation should re-arm on every poll.</summary>
        public void Request(int waiterPid, TimeSpan lease)
        {
            try
            {
                if (_view == null) return;
                long expiry = DateTime.UtcNow.Add(lease).Ticks;
                // Several windows may be waiting at once — never shorten someone else's lease.
                long current = _view.ReadInt64(OffExpiry);
                if (current > expiry) return;
                _view.Write(OffExpiry, expiry);
                _view.Write(OffPid, waiterPid);
                _view.Write(OffVersion, LayoutVersion);
            }
            catch { }
        }

        /// <summary>True while some client's lease is still running. Aligned 8-byte reads are atomic
        /// on the platforms this ships to, so no cross-process lock is needed for a torn-read-free
        /// answer; a stale read is self-correcting on the next poll anyway.</summary>
        public bool IsActive(out int waiterPid, out TimeSpan remaining)
        {
            waiterPid = 0;
            remaining = TimeSpan.Zero;
            try
            {
                if (_view == null) return false;
                long expiry = _view.ReadInt64(OffExpiry);
                if (expiry <= 0) return false;
                long now = DateTime.UtcNow.Ticks;
                if (expiry <= now) return false;
                waiterPid = _view.ReadInt32(OffPid);
                remaining = TimeSpan.FromTicks(expiry - now);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Drop the lease immediately, rather than letting it lapse.</summary>
        public void Clear()
        {
            try { _view?.Write(OffExpiry, 0L); } catch { }
        }

        public void Dispose()
        {
            try { _view?.Dispose(); } catch { }
            try { _mmf?.Dispose(); } catch { }
            _view = null;
            _mmf = null;
        }
    }
}
