using VitaSharp.Core.Util;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace VitaSharp.Core.Memory;

/// <summary>
/// Page-granular (4 KB) record of guest memory writes, so consumers (the renderer's texture cache) only re-read memory
/// that may have changed instead of hashing it every frame.
///
/// Producers set one byte per page in a 1 MB table that sits right below guest address 0 (inside the guest
/// reservation, so native code addresses it as <c>[guest base + (addr &gt;&gt; 12) - TableBytes]</c>):
/// <list type="bullet">
/// <item>CPU stores of every backend (interpreter and IL JIT through <c>Mem.W*</c>, the native x64 JIT inline after each
/// store, exclusive stores and SWP);</item>
/// <item>HLE writes: <see cref="GuestMemory.Write{T}"/>, <see cref="GuestMemory.Span(uint, int)"/> (writable spans are
/// marked conservatively when created), <see cref="GuestMemory.Ref{T}"/>, <see cref="Ptr{T}"/>, Copy/Fill/WriteCString,
/// block allocation and free;</item>
/// <item>GPU writebacks into guest memory (they go through writable spans).</item>
/// </list>
/// Producers mark AFTER the data store, and only when the byte is still clear (a load first keeps the cache line shared
/// between cores while a page stays dirty).
///
/// The single consumer (the GPU worker thread) calls <see cref="Sweep"/> to fold the table into per-page versions
/// and clear it, then asks <see cref="WrittenSince"/> whether a range changed after the sweep it last validated at.
/// A page found dirty in sweep N gets version N, and N also counts as "written" for anything validated after sweep N
/// (one sweep of grace): a store still sitting in another core's store buffer when its page was swept and cleared is
/// visible by the following sweep. A store can reach into the page after the one it marks (unaligned or 8/16-byte
/// stores at a page end), so the sweep gives the next page the same version.
/// </summary>
public static unsafe class DirtyPages
{
    public const int PageShift = 12;
    public const uint Pages = 1u << (32 - PageShift);
    public const ulong TableBytes = Pages;

    /// <summary>VITASHARP_TEXDIRTY: "0" = off (consumers re-check everything, as before), "verify" = consumers re-check
    /// everything and report changes the tracker missed, anything else = on.</summary>
    public static readonly string Mode = Environment.GetEnvironmentVariable(EnvVars.Texdirty) ?? "1";
    /// <summary>Producers mark pages (static readonly: the JIT drops the marking entirely when off).</summary>
    public static readonly bool Enabled = Mode != "0";
    /// <summary>Consumers re-check everything and compare (debug).</summary>
    public static readonly bool Verify = Mode == "verify";

    /// <summary>The byte table (one byte per guest page), right below guest address 0.</summary>
    public static byte* Table => GuestMemory.Base - TableBytes;

    static readonly uint[] _version = new uint[Pages];
    static uint _epoch;

    /// <summary>Number of sweeps so far: the stamp to remember for data validated now.</summary>
    public static uint Epoch => _epoch;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mark(uint addr)
    {
        if (!Enabled) return;
        byte* p = GuestMemory.Base - TableBytes + (addr >> PageShift);
        if (*p == 0) *p = 1;
    }

    public static void MarkRange(uint addr, uint length)
    {
        if (!Enabled || length == 0 || GuestMemory.Base == null) return;
        byte* t = GuestMemory.Base - TableBytes;
        ulong last = Math.Min((ulong)addr + length - 1, uint.MaxValue);
        for (ulong p = addr >> PageShift; p <= last >> PageShift; p++)
            if (t[p] == 0) t[p] = 1;
    }

    /// <summary>
    /// Folds the table into the page versions and clears it. Consumer thread only. Returns the new epoch: anything
    /// validated from now on is stamped with it.
    /// </summary>
    public static uint Sweep()
    {
        uint e = ++_epoch;
        if (!Enabled || GuestMemory.Base == null) return e;
        byte* t = Table;
        fixed (uint* ver = _version)
        {
            for (uint i = 0; i < Pages; i += 64)
            {
                var v = Vector128.Load(t + i) | Vector128.Load(t + i + 16) | Vector128.Load(t + i + 32) | Vector128.Load(t + i + 48);
                if (v == Vector128<byte>.Zero) continue;
                for (uint j = i; j < i + 64; j++)
                {
                    if (t[j] == 0) continue;
                    t[j] = 0;
                    ver[j] = e;
                    if (j + 1 < Pages) ver[j + 1] = e;
                }
            }
        }
        return e;
    }

    /// <summary>
    /// True when a page of [addr, addr + length) may have been written after the sweep <paramref name="stamp"/> was
    /// taken from (see the class remarks for the one-sweep grace). Always true when tracking is off.
    /// </summary>
    public static bool WrittenSince(uint addr, uint length, uint stamp)
    {
        if (!Enabled) return true;
        if (length == 0) return false;
        ulong last = Math.Min((ulong)addr + length - 1, uint.MaxValue);
        int first = (int)(addr >> PageShift), count = (int)(last >> PageShift) - first + 1;
        // any version >= stamp, i.e. outside [0, stamp - 1]
        return stamp == 0 || _version.AsSpan(first, count).IndexOfAnyExceptInRange(0u, stamp - 1) >= 0;
    }
}
